using System.Diagnostics;
using System.Globalization;
using BenchForge.Core.Gpu;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace BenchForge.Gpu;

/// <summary>
/// The 3D test process. Renders each scene at a fixed internal resolution (so scores don't depend on window size) with the
/// chosen graphics API, shows it in a window with a progress bar, and reports JSON lines on stdout to BenchForge.
/// </summary>
public static class GpuProgram
{
    private static volatile bool _stopRequested;
    /// <summary>PSU spike test: the app pauses and resumes rendering in step with the CPU load.</summary>
    private static volatile bool _paused;
    private static long _vramChecked, _vramErrors, _vramPassBytes;

    public static int Run(string[] args)
    {
        var a = ParseArgs(args);
        string mode = a.GetValueOrDefault("mode", "bench");
        string api = a.GetValueOrDefault("api", GpuApis.OpenGL);
        double duration = double.Parse(a.GetValueOrDefault("duration", "60"), CultureInfo.InvariantCulture);
        int width = int.Parse(a.GetValueOrDefault("width", "1920"), CultureInfo.InvariantCulture), height = int.Parse(a.GetValueOrDefault("height", "1080"), CultureInfo.InvariantCulture);
        bool fullscreen = a.ContainsKey("fullscreen"), headless = a.ContainsKey("headless"), software = a.ContainsKey("software");
        bool stress = mode == "stress";
        string? shotDir = a.GetValueOrDefault("screenshots");

        var stdinThread = new Thread(() =>
        {
            try
            {
                string? line;
                while ((line = Console.In.ReadLine()) != null)
                {
                    var cmd = line.Trim();
                    if (cmd == "stop") { _stopRequested = true; break; }
                    if (cmd == "pause") _paused = true; else if (cmd == "resume") _paused = false;
                }
            }
            catch { }
        }) { IsBackground = true };
        stdinThread.Start();

        IGpuBackend backend;
        try { backend = CreateBackend(api); }
        catch (Exception ex) { Send(new GpuMessage { Type = "error", Message = $"{GpuApis.Display(api)} is not available: {ex.Message}" }); return 2; }

        IWindow? window = null;
        try
        {
            if (!headless || api == GpuApis.OpenGL)
            {
                var options = backend.ConfigureWindow(WindowOptions.Default with
                {
                    Size = new Vector2D<int>(1280, 720),
                    Title = $"BenchForge 3D test — {GpuApis.Display(api)}",
                    VSync = false,
                    FramesPerSecond = 0,
                    UpdatesPerSecond = 0,
                    IsVisible = !headless,
                    // "Full screen" is borderless + maximised (not exclusive) so the BenchForge overlay can stay on top.
                    WindowState = fullscreen ? WindowState.Maximized : WindowState.Normal,
                    WindowBorder = fullscreen ? WindowBorder.Hidden : WindowBorder.Resizable,
                });
                PreloadGlfw();
                Silk.NET.Windowing.Glfw.GlfwWindowing.RegisterPlatform();
                Silk.NET.Windowing.Glfw.GlfwWindowing.Use();
                window = Window.Create(options);
                window.Initialize();
            }
        }
        catch (Exception ex) { Send(new GpuMessage { Type = "error", Message = "Could not create a window: " + ex.Message }); return 2; }

        string[] order = stress ? new[] { GpuScenes.Shading, GpuScenes.Compute, GpuScenes.Geometry, GpuScenes.Fill } : GpuScenes.Benchmark;
        try
        {
            backend.Init(window, width, height, software);
            Send(new GpuMessage { Type = "info", Api = api, Renderer = backend.Renderer, Vendor = backend.Vendor, Version = backend.Version, Width = width, Height = height });
            backend.CreateScenes(order);
            _vramPassBytes = backend.InitVram(stress ? 1024L << 20 : 512L << 20);
            Send(new GpuMessage { Type = "log", Message = $"VRAM test area: {_vramPassBytes >> 20} MB" });
        }
        catch (Exception ex)
        {
            Send(new GpuMessage { Type = "error", Message = $"3D setup failed ({GpuApis.Display(api)}): {ex.Message}" });
            try { backend.Dispose(); } catch { }
            try { window?.Dispose(); } catch { }
            return 3;
        }

        int exitCode = 0;
        try { Loop(backend, window, order, duration, width, height, stress, shotDir); }
        catch (Exception ex)
        {
            Send(new GpuMessage { Type = "error", Message = $"3D test error ({GpuApis.Display(api)}): {ex.Message}" });
            exitCode = 4;
        }
        finally
        {
            try { backend.WaitIdle(); } catch { }
            try { backend.Dispose(); } catch { }
            try { window?.Dispose(); } catch { }
        }
        return exitCode;
    }

    private static IGpuBackend CreateBackend(string api) => api switch
    {
        GpuApis.OpenGL => new GlBackend(),
        GpuApis.Vulkan => new VulkanBackend(),
        GpuApis.D3D11 when OperatingSystem.IsWindows() => new D3D11Backend(),
        GpuApis.D3D12 when OperatingSystem.IsWindows() => new D3D12Backend(),
        GpuApis.D3D11 or GpuApis.D3D12 => throw new PlatformNotSupportedException("DirectX needs Windows"),
        _ => throw new ArgumentException("unknown graphics API " + api),
    };

    private static void Loop(IGpuBackend backend, IWindow? window, string[] order, double duration, int width, int height, bool stress, string? shotDir)
    {
        var shotTaken = new HashSet<int>();
        int sceneIndex = 0;
        var sceneClock = Stopwatch.StartNew();
        var totalClock = Stopwatch.StartNew();
        var frameClock = Stopwatch.StartNew();
        var frameTimes = new List<double>(100_000);
        long sceneFrames = 0;
        double lastReport = 0;
        double fpsWindowStart = 0; long fpsWindowFrames = 0; double currentFps = 0;
        double nextVramCheck = 20;
        bool pcieDone = false;
        // Benchmark: equal slots per scene, then a short VRAM + PCIe check. Stress: rotate scenes every 30 s with VRAM checks every 20 s.
        double vramSlot = stress ? 0 : Math.Min(6, duration * 0.08);
        double sceneSlot = stress ? 30 : (duration - vramSlot) / order.Length;
        double warmup = stress ? 0 : Math.Min(1.0, sceneSlot * 0.1);
        int count = order.Length + (vramSlot > 0 ? 1 : 0);

        void FinishScene()
        {
            if (stress || sceneFrames == 0) return;
            var sorted = frameTimes.OrderBy(x => x).ToList();
            double avgMs = sorted.Average();
            double p99 = sorted[(int)Math.Min(sorted.Count - 1, sorted.Count * 0.99)];
            var worst = sorted.Skip((int)(sorted.Count * 0.99)).DefaultIfEmpty(avgMs).Average();
            Send(new GpuMessage
            {
                Type = "scene", Scene = order[sceneIndex], Frames = frameTimes.Count, Seconds = frameTimes.Sum() / 1000.0,
                AvgFps = 1000.0 / avgMs, Low1Fps = 1000.0 / worst, P99Ms = p99,
            });
        }

        void ReportProgress(double now, string scene, int index, int cnt, double frac, double fps)
        {
            if (now - lastReport < 0.5) return;
            lastReport = now;
            Send(new GpuMessage { Type = "progress", Scene = scene, Index = index, Count = cnt, Fraction = Math.Clamp(frac, 0, 1), Fps = Math.Round(fps, 1) });
            if (window != null)
            {
                var left = TimeSpan.FromSeconds(Math.Max(0, duration - now));
                window.Title = $"BenchForge 3D ({GpuApis.Display(backend.Api)}) — {scene} — {fps:0} FPS — {(int)left.TotalMinutes}:{left.Seconds:00} left";
            }
        }

        while (true)
        {
            window?.DoEvents();
            if (window?.IsClosing == true) _stopRequested = true;
            double now = totalClock.Elapsed.TotalSeconds;
            if (_stopRequested || now >= duration)
            {
                if (!_stopRequested && sceneIndex < order.Length) FinishScene();
                if (!stress && !_stopRequested) RunVram(backend);
                Send(new GpuMessage { Type = "done" });
                return;
            }

            if (_paused)
            {
                // Idle half of a load spike: no GPU work, keep the window responsive.
                backend.BeginFrame();
                backend.EndFrame(false, now / duration, 0, 0);
                ReportProgress(now, "Idle (load spike pause)", 0, 1, now / duration, 0);
                Thread.Sleep(10);
                frameClock.Restart();
                continue;
            }

            double sceneTime = sceneClock.Elapsed.TotalSeconds;
            if (!stress && sceneIndex < order.Length && sceneTime >= sceneSlot)
            {
                FinishScene();
                sceneIndex++;
                frameTimes.Clear(); sceneFrames = 0;
                sceneClock.Restart(); sceneTime = 0;
            }
            if (stress && sceneTime >= sceneSlot) { sceneIndex = (sceneIndex + 1) % order.Length; sceneClock.Restart(); sceneTime = 0; }
            if (stress && now >= nextVramCheck) { RunVram(backend); nextVramCheck = now + 20; }

            if (sceneIndex >= order.Length)
            {
                // Benchmark: the last slot checks the PCIe link speed and video memory.
                if (!pcieDone)
                {
                    pcieDone = true;
                    try
                    {
                        var (up, down) = backend.TransferTest(256L << 20);
                        Send(new GpuMessage { Type = "pcie", Scene = GpuScenes.Pcie, UploadGBs = Math.Round(up, 2), DownloadGBs = Math.Round(down, 2) });
                    }
                    catch (Exception ex) { Send(new GpuMessage { Type = "log", Message = "PCIe transfer test failed: " + ex.Message }); }
                }
                RunVram(backend);
                backend.BeginFrame();
                backend.EndFrame(false, now / duration, 0, 0);
                ReportProgress(now, GpuScenes.Vram, order.Length, count, Math.Clamp((now - sceneSlot * order.Length) / Math.Max(0.1, vramSlot), 0, 1), 0);
                continue;
            }

            backend.BeginFrame();
            backend.RenderScene(sceneIndex, now);
            if (shotDir != null && !shotTaken.Contains(sceneIndex) && sceneTime > sceneSlot * 0.5)
            {
                shotTaken.Add(sceneIndex);
                backend.WaitIdle();
                SceneMath.SaveBmp(Path.Combine(shotDir, $"{backend.Api}-{sceneIndex + 1}-{order[sceneIndex]}.bmp"), backend.ReadPixels(), width, height);
            }
            backend.EndFrame(true, now / duration, stress ? 0 : sceneIndex, stress ? 0 : order.Length);
            if (window == null) backend.WaitIdle(); // headless: no swap chain to pace frames, so time each frame to completion

            double ms = frameClock.Elapsed.TotalMilliseconds;
            frameClock.Restart();
            if (sceneTime > warmup) { frameTimes.Add(ms); sceneFrames++; }
            fpsWindowFrames++;
            if (now - fpsWindowStart >= 0.5) { currentFps = fpsWindowFrames / (now - fpsWindowStart); fpsWindowStart = now; fpsWindowFrames = 0; }
            if (stress && frameTimes.Count > 50_000) frameTimes.Clear();
            ReportProgress(now, order[sceneIndex], stress ? 0 : sceneIndex, stress ? 1 : count, stress ? now / duration : sceneTime / sceneSlot, currentFps);
        }
    }

    /// <summary>
    /// In the single-file exe, native DLLs are unpacked to a temp folder. Load GLFW from there up front so the
    /// windowing library always finds it (a loaded module is reused by name).
    /// </summary>
    private static void PreloadGlfw()
    {
        var name = OperatingSystem.IsWindows() ? "glfw3.dll" : "libglfw.so.3";
        var dirs = new List<string> { AppContext.BaseDirectory };
        if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string extra)
            dirs.AddRange(extra.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        dirs.Add(Path.Combine(AppContext.BaseDirectory, "runtimes", OperatingSystem.IsWindows() ? "win-x64" : "linux-x64", "native"));
        foreach (var d in dirs)
        {
            var p = Path.Combine(d, name);
            if (File.Exists(p) && System.Runtime.InteropServices.NativeLibrary.TryLoad(p, out _)) return;
        }
    }

    private static void RunVram(IGpuBackend backend)
    {
        var (errors, detail) = backend.VramPass();
        _vramErrors += errors;
        _vramChecked += _vramPassBytes;
        Send(new GpuMessage { Type = "vram", Errors = _vramErrors, Checked = _vramChecked, Message = detail });
    }

    private static readonly object OutLock = new();
    internal static void Send(GpuMessage m)
    {
        lock (OutLock) { Console.Out.WriteLine(m.ToLine()); Console.Out.Flush(); }
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var d = new Dictionary<string, string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) continue;
            var key = args[i][2..];
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) d[key] = args[++i];
            else d[key] = "true";
        }
        return d;
    }
}
