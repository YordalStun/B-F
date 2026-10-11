using System.Diagnostics;
using System.Globalization;
using BenchForge.Core.Engine;
using BenchForge.Core.Model;
using BenchForge.Core.Scoring;

namespace BenchForge.Core.Gpu;

/// <summary>
/// Runs the OpenGL 3D test in a separate process. If the graphics driver crashes, only that process dies and
/// the failure is recorded instead of taking BenchForge down with it.
/// </summary>
public sealed class GpuModule : ITestModule
{
    /// <summary>Creates the start info for the 3D worker given its arguments. Set by the host app.</summary>
    public static Func<string, ProcessStartInfo> LaunchFactory { get; set; } = DefaultLaunch;

    public Component Component => Component.Gpu;
    public const int InternalWidth = 1920, InternalHeight = 1080;

    public static ProcessStartInfo DefaultLaunch(string args)
    {
        var baseDir = AppContext.BaseDirectory;
        var exe = Path.Combine(baseDir, OperatingSystem.IsWindows() ? "BenchForge.Gpu.exe" : "BenchForge.Gpu");
        if (File.Exists(exe)) return new ProcessStartInfo(exe, args);
        var dll = Path.Combine(baseDir, "BenchForge.Gpu.dll");
        if (File.Exists(dll)) return new ProcessStartInfo("dotnet", $"\"{dll}\" {args}");
        // Same executable with a worker switch (single-file app).
        return new ProcessStartInfo(Environment.ProcessPath ?? "BenchForge", "--gpu-worker " + args);
    }

    public async Task<ModuleResult> RunAsync(StepContext ctx)
    {
        var result = new ModuleResult { Component = Component.Gpu, Kind = ctx.Kind };
        var s = ctx.Settings;
        double seconds = Math.Max(5, ctx.DurationSeconds - 3);
        // The internal render resolution is fixed so scores are comparable on every PC, whatever the window or monitor size.
        var args = string.Create(CultureInfo.InvariantCulture,
            $"--mode {(ctx.Kind == StepKind.Benchmark ? "bench" : "stress")} --duration {seconds:0.0} --width {InternalWidth} --height {InternalHeight}{(s.GpuFullscreen ? " --fullscreen" : "")}");
        var psi = LaunchFactory(args);
        psi.UseShellExecute = false;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardInput = true;
        psi.CreateNoWindow = true;

        result.Info["Internal resolution"] = $"{InternalWidth}×{InternalHeight} (fixed)";
        var scenes = new List<GpuMessage>();
        var stderr = new List<string>();
        long vramErrors = 0, vramChecked = 0;
        bool done = false;
        var lastMessage = DateTime.Now;
        string? currentScene = null;

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            var m = GpuMessage.Parse(e.Data);
            if (m == null) return;
            lastMessage = DateTime.Now;
            switch (m.Type)
            {
                case "info":
                    result.Info["Renderer"] = m.Renderer ?? "";
                    result.Info["Vendor"] = m.Vendor ?? "";
                    result.Info["OpenGL"] = m.Version ?? "";
                    ctx.Log($"3D test running on {m.Renderer} ({m.Version})");
                    break;
                case "progress":
                    currentScene = m.Scene;
                    double overall = m.Count > 0 ? (m.Index + m.Fraction) / m.Count : m.Fraction;
                    if (ctx.Kind == StepKind.Stress) overall = ctx.Elapsed.TotalSeconds / ctx.DurationSeconds;
                    ctx.Progress($"{m.Scene} — {m.Fps:0} FPS", overall);
                    ctx.Metric("gpu.fps", "3D test FPS", Math.Round(m.Fps, 1), "FPS");
                    break;
                case "scene":
                    lock (scenes) scenes.Add(m);
                    ctx.Log($"{m.Scene}: {m.AvgFps:0.0} FPS average, 1% low {m.Low1Fps:0.0} FPS");
                    break;
                case "vram":
                    vramChecked = m.Checked;
                    if (m.Errors > vramErrors)
                        ctx.Error(new TestError { Test = GpuScenes.Vram, Message = $"{m.Errors - vramErrors} video memory errors detected", Detail = m.Message });
                    vramErrors = m.Errors;
                    break;
                case "error":
                    ctx.Error(new TestError { Test = m.Scene ?? "3D test", Message = m.Message ?? "GPU error" });
                    break;
                case "log":
                    ctx.Log(m.Message ?? "");
                    break;
                case "done":
                    done = true;
                    break;
            }
        };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (stderr) { if (stderr.Count < 50) stderr.Add(e.Data); } };

        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            result.Failed = true;
            result.FailureReason = "Could not start the 3D test: " + ex.Message;
            ctx.Error(new TestError { Test = "3D test", Message = result.FailureReason });
            return result;
        }
        try { proc.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        bool hung = false;
        while (!proc.HasExited)
        {
            try { await Task.Delay(250, ctx.Token); } catch (OperationCanceledException) { break; }
            if ((DateTime.Now - lastMessage).TotalSeconds > 20)
            {
                hung = true;
                break;
            }
        }
        if (!proc.HasExited)
        {
            try { proc.StandardInput.WriteLine("stop"); proc.StandardInput.Flush(); } catch { }
            if (!proc.WaitForExit(4000)) { try { proc.Kill(true); } catch { } }
        }
        try { proc.WaitForExit(2000); } catch { }

        int exitCode = 0;
        try { exitCode = proc.ExitCode; } catch { }

        if (hung)
        {
            result.Failed = true;
            result.FailureReason = $"The 3D test stopped responding during '{currentScene}'. This usually means the GPU driver hung or reset (TDR).";
            ctx.Error(new TestError { Test = currentScene ?? "3D test", Message = result.FailureReason });
        }
        else if (!done && !ctx.Token.IsCancellationRequested)
        {
            result.Failed = true;
            result.FailureReason = $"The 3D test process ended unexpectedly (exit code {exitCode}) during '{currentScene ?? "startup"}'. " +
                                   "This usually means the graphics driver crashed, the GPU is unstable, or OpenGL 4.3 is not supported.";
            lock (stderr) if (stderr.Count > 0) result.Info["Process output"] = string.Join(" | ", stderr.Take(5));
            ctx.Error(new TestError { Test = currentScene ?? "3D test", Message = result.FailureReason });
        }

        result.ErrorCount = (int)vramErrors + (result.Failed ? 1 : 0);
        if (vramChecked > 0) result.Info["VRAM verified"] = $"{vramChecked / 1e9:0.0} GB, {vramErrors} errors";

        lock (scenes)
        {
            foreach (var sc in scenes)
            {
                if (sc.Scene == null) continue;
                double score = ScoreReferences.Gpu.TryGetValue(sc.Scene, out var r) ? Score.Linear(sc.AvgFps, r) : 0;
                result.SubScores.Add(new SubScore
                {
                    Name = sc.Scene, Group = ctx.Kind == StepKind.Benchmark ? "3D scenes" : "Stress",
                    Value = Math.Round(sc.AvgFps, 1), Unit = "FPS", Score = Math.Round(score),
                    Detail = $"1% low {sc.Low1Fps:0.0} FPS · 99th percentile frame {sc.P99Ms:0.0} ms · {sc.Frames} frames",
                });
            }
            if (ctx.Kind == StepKind.Benchmark)
            {
                var sceneScores = result.SubScores.Where(x => x.Score > 0).Select(x => x.Score).ToList();
                if (sceneScores.Count == GpuScenes.Benchmark.Length && !result.Failed)
                    result.Score = Math.Round(Score.GeoMean(sceneScores));
            }
        }
        ctx.Progress("Done", 1);
        return result;
    }
}
