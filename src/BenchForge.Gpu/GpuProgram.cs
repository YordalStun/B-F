using System.Diagnostics;
using System.Globalization;
using BenchForge.Core.Gpu;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace BenchForge.Gpu;

/// <summary>
/// The 3D test process. Renders each scene at a fixed internal resolution (so scores don't depend on window size),
/// shows it in a window with a progress bar, and reports JSON lines on stdout to BenchForge.
/// </summary>
public static class GpuProgram
{
    private static volatile bool _stopRequested;

    public static unsafe int Run(string[] args)
    {
        var a = ParseArgs(args);
        string mode = a.GetValueOrDefault("mode", "bench");
        double duration = double.Parse(a.GetValueOrDefault("duration", "60"), CultureInfo.InvariantCulture);
        int width = int.Parse(a.GetValueOrDefault("width", "1920")), height = int.Parse(a.GetValueOrDefault("height", "1080"));
        bool fullscreen = a.ContainsKey("fullscreen");
        bool stress = mode == "stress";
        string? shotDir = a.GetValueOrDefault("screenshots");
        var shotTaken = new HashSet<int>();

        var stdinThread = new Thread(() =>
        {
            try { string? line; while ((line = Console.In.ReadLine()) != null) if (line.Trim() == "stop") { _stopRequested = true; break; } }
            catch { }
        }) { IsBackground = true };
        stdinThread.Start();

        var options = WindowOptions.Default with
        {
            Size = new Vector2D<int>(1280, 720),
            Title = "BenchForge 3D test",
            VSync = false,
            FramesPerSecond = 0,
            UpdatesPerSecond = 0,
            // "Full screen" is borderless + maximised (not exclusive) so the BenchForge overlay can stay on top.
            WindowState = fullscreen ? WindowState.Maximized : WindowState.Normal,
            WindowBorder = fullscreen ? WindowBorder.Hidden : WindowBorder.Resizable,
            API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(4, 3)),
        };

        PreloadGlfw();
        IWindow window;
        try
        {
            if (Environment.GetEnvironmentVariable("BF_GLFW_DEBUG") == "1")
            {
                try { var api = Silk.NET.GLFW.Glfw.GetApi(); Console.Error.WriteLine("glfw init: " + api.Init()); api.GetError(out byte* desc); Console.Error.WriteLine("err: " + System.Runtime.InteropServices.Marshal.PtrToStringAnsi((nint)desc)); }
                catch (Exception ex) { Console.Error.WriteLine("GLFW load failed: " + ex); }
            }
            Silk.NET.Windowing.Glfw.GlfwWindowing.RegisterPlatform();
            Silk.NET.Windowing.Glfw.GlfwWindowing.Use();
            window = Window.Create(options);
        }
        catch (Exception ex) { Send(new GpuMessage { Type = "error", Message = "Could not create a window: " + ex.Message }); return 2; }

        GL? gl = null;
        uint fbo = 0, colorTex = 0, depthRb = 0;
        var scenes = new List<IScene>();
        VramChecker? vram = null;
        int sceneIndex = 0;
        var sceneClock = Stopwatch.StartNew();
        var totalClock = Stopwatch.StartNew();
        var frameClock = Stopwatch.StartNew();
        var frameTimes = new List<double>(100_000);
        long sceneFrames = 0;
        double lastReport = 0;
        double fpsWindowStart = 0; long fpsWindowFrames = 0; double currentFps = 0;
        double nextVramCheck = 20;
        int exitCode = 0;
        string[] order = stress
            ? new[] { GpuScenes.Shading, GpuScenes.Compute, GpuScenes.Geometry, GpuScenes.Fill }
            : GpuScenes.Benchmark;
        // Benchmark: equal slots per scene, then a short VRAM check. Stress: rotate scenes every 30 s with VRAM checks every 20 s.
        double vramSlot = stress ? 0 : Math.Min(6, duration * 0.08);
        double sceneSlot = stress ? 30 : (duration - vramSlot) / order.Length;
        double warmup = stress ? 0 : Math.Min(1.0, sceneSlot * 0.1);

        window.Load += () =>
        {
            try
            {
                gl = window.CreateOpenGL();
                Send(new GpuMessage { Type = "info", Renderer = gl.GetStringS(StringName.Renderer), Vendor = gl.GetStringS(StringName.Vendor), Version = gl.GetStringS(StringName.Version), Width = width, Height = height });
                (fbo, colorTex, depthRb) = CreateTarget(gl, width, height);
                foreach (var name in order)
                {
                    IScene s = name switch
                    {
                        GpuScenes.Geometry => new GeometryScene(),
                        GpuScenes.Shading => new ShadingScene(),
                        GpuScenes.Compute => new ComputeScene(),
                        _ => new FillScene(),
                    };
                    s.Init(gl, width, height);
                    scenes.Add(s);
                }
                vram = new VramChecker();
                vram.Init(gl, stress ? 1024L << 20 : 512L << 20);
                Send(new GpuMessage { Type = "log", Message = $"VRAM test area: {vram.BytesPerPass >> 20} MB" });
                sceneClock.Restart(); totalClock.Restart(); frameClock.Restart();
            }
            catch (Exception ex)
            {
                Send(new GpuMessage { Type = "error", Message = "3D setup failed: " + ex.Message });
                exitCode = 3;
                window.Close();
            }
        };

        void FinishScene()
        {
            if (stress || sceneFrames == 0) return;
            var sorted = frameTimes.OrderBy(x => x).ToList();
            double avgMs = sorted.Average();
            double p99 = sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * 0.99))];
            double low1 = 1000.0 / sorted.Skip((int)(sorted.Count * 0.99)).DefaultIfEmpty(p99).Average();
            Send(new GpuMessage
            {
                Type = "scene", Scene = scenes[sceneIndex].Name, Frames = frameTimes.Count, Seconds = frameTimes.Sum() / 1000.0,
                AvgFps = 1000.0 / avgMs, Low1Fps = low1, P99Ms = p99,
            });
        }

        window.Render += _ =>
        {
            if (gl == null || scenes.Count == 0) return;
            double now = totalClock.Elapsed.TotalSeconds;
            if (_stopRequested || now >= duration)
            {
                if (!_stopRequested && sceneIndex < scenes.Count) FinishScene();
                if (vram != null && !stress && !_stopRequested)
                {
                    // Final VRAM check at the end of the benchmark.
                    RunVram(gl, vram);
                }
                Send(new GpuMessage { Type = "done" });
                window.Close();
                return;
            }

            // Scene scheduling.
            double sceneTime = sceneClock.Elapsed.TotalSeconds;
            if (!stress && sceneIndex < scenes.Count && sceneTime >= sceneSlot)
            {
                FinishScene();
                sceneIndex++;
                frameTimes.Clear(); sceneFrames = 0;
                sceneClock.Restart(); sceneTime = 0;
            }
            if (stress && sceneTime >= sceneSlot) { sceneIndex = (sceneIndex + 1) % scenes.Count; sceneClock.Restart(); sceneTime = 0; }
            if (stress && vram != null && now >= nextVramCheck) { RunVram(gl, vram); nextVramCheck = now + 20; }

            if (sceneIndex >= scenes.Count)
            {
                // Benchmark: remaining time is the VRAM check slot.
                if (vram != null) RunVram(gl, vram);
                DrawOverlay(gl, window, now / duration, 0, 0);
                ReportProgress(now, GpuScenes.Vram, order.Length, order.Length + 1, Math.Clamp((now - sceneSlot * order.Length) / Math.Max(0.1, vramSlot), 0, 1), 0);
                return;
            }

            var scene = scenes[sceneIndex];
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            gl.Viewport(0, 0, (uint)width, (uint)height);
            scene.Render(gl, now, width, height);
            // Show it: scale the internal image into the window.
            var fb = window.FramebufferSize;
            gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, fbo);
            gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, 0);
            gl.BlitFramebuffer(0, 0, width, height, 0, 0, fb.X, fb.Y, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Linear);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            if (shotDir != null && !shotTaken.Contains(sceneIndex) && sceneTime > sceneSlot * 0.5)
            {
                shotTaken.Add(sceneIndex);
                SaveScreenshot(gl, fbo, width, height, Path.Combine(shotDir, $"{sceneIndex + 1}-{scene.Name}.bmp"));
            }
            DrawOverlay(gl, window, now / duration, stress ? 0 : sceneIndex, stress ? 0 : scenes.Count);

            double ms = frameClock.Elapsed.TotalMilliseconds;
            frameClock.Restart();
            if (sceneTime > warmup) { frameTimes.Add(ms); sceneFrames++; }
            fpsWindowFrames++;
            if (now - fpsWindowStart >= 0.5) { currentFps = fpsWindowFrames / (now - fpsWindowStart); fpsWindowStart = now; fpsWindowFrames = 0; }
            if (stress && frameTimes.Count > 50_000) frameTimes.Clear();
            ReportProgress(now, scene.Name, stress ? 0 : sceneIndex, stress ? 1 : order.Length + (vramSlot > 0 ? 1 : 0), stress ? now / duration : sceneTime / sceneSlot, currentFps);
        };

        void ReportProgress(double now, string scene, int index, int count, double frac, double fps)
        {
            if (now - lastReport < 0.5) return;
            lastReport = now;
            Send(new GpuMessage { Type = "progress", Scene = scene, Index = index, Count = count, Fraction = Math.Clamp(frac, 0, 1), Fps = Math.Round(fps, 1) });
            var left = TimeSpan.FromSeconds(Math.Max(0, duration - now));
            window.Title = $"BenchForge 3D — {scene} — {fps:0} FPS — {(int)left.TotalMinutes}:{left.Seconds:00} left";
        }

        window.Closing += () =>
        {
            foreach (var s in scenes) s.Dispose();
        };

        try
        {
            window.Run();
        }
        catch (Exception ex)
        {
            Send(new GpuMessage { Type = "error", Message = "3D test error: " + ex.Message });
            return 4;
        }
        finally
        {
            try { window.Dispose(); } catch { }
        }
        return exitCode;
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

    private static void RunVram(GL gl, VramChecker vram)
    {
        var (errors, detail) = vram.RunPass(gl);
        Send(new GpuMessage { Type = "vram", Errors = vram.TotalErrors, Checked = vram.TotalChecked, Message = detail });
    }

    private static unsafe void SaveScreenshot(GL gl, uint fbo, int w, int h, string path)
    {
        var px = new byte[w * h * 4];
        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, fbo);
        fixed (byte* p = px) gl.ReadPixels(0, 0, (uint)w, (uint)h, PixelFormat.Bgra, PixelType.UnsignedByte, p);
        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        bw.Write((byte)'B'); bw.Write((byte)'M'); bw.Write(54 + px.Length); bw.Write(0); bw.Write(54);
        bw.Write(40); bw.Write(w); bw.Write(h); bw.Write((short)1); bw.Write((short)32); bw.Write(0); bw.Write(px.Length); bw.Write(2835); bw.Write(2835); bw.Write(0); bw.Write(0);
        bw.Write(px);
    }

    private static (uint Fbo, uint Tex, uint Depth) CreateTarget(GL gl, int w, int h)
    {
        uint fbo = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        uint tex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, tex);
        unsafe { gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)w, (uint)h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, null); }
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, tex, 0);
        uint rb = gl.GenRenderbuffer();
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, rb);
        gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.Depth24Stencil8, (uint)w, (uint)h);
        gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthStencilAttachment, RenderbufferTarget.Renderbuffer, rb);
        var status = gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete) throw new InvalidOperationException("Framebuffer incomplete: " + status);
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        return (fbo, tex, rb);
    }

    /// <summary>Progress bar along the bottom of the window (scissored clears — no text rendering needed).</summary>
    private static void DrawOverlay(GL gl, IWindow window, double overall, int scene, int scenes)
    {
        var fb = window.FramebufferSize;
        int barH = Math.Max(6, fb.Y / 90);
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        gl.Enable(EnableCap.ScissorTest);
        gl.Scissor(0, 0, (uint)fb.X, (uint)barH);
        gl.ClearColor(0.08f, 0.09f, 0.11f, 1); gl.Clear(ClearBufferMask.ColorBufferBit);
        gl.Scissor(0, 0, (uint)Math.Clamp(fb.X * overall, 0, fb.X), (uint)barH);
        gl.ClearColor(1f, 0.48f, 0.24f, 1); gl.Clear(ClearBufferMask.ColorBufferBit);
        for (int i = 1; i < scenes; i++)
        {
            gl.Scissor(fb.X * i / scenes - 1, 0, 2, (uint)barH);
            gl.ClearColor(0, 0, 0, 1); gl.Clear(ClearBufferMask.ColorBufferBit);
        }
        gl.Disable(EnableCap.ScissorTest);
    }

    private static readonly object OutLock = new();
    private static void Send(GpuMessage m)
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
