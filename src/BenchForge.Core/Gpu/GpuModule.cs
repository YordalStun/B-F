using System.Diagnostics;
using System.Globalization;
using BenchForge.Core.Engine;
using BenchForge.Core.Model;
using BenchForge.Core.Scoring;

namespace BenchForge.Core.Gpu;

/// <summary>
/// Runs the 3D test in a separate process, on one graphics API or each of them in turn. If the graphics driver crashes,
/// only that process dies and the failure is recorded instead of taking BenchForge down with it.
/// </summary>
public sealed class GpuModule : ITestModule
{
    /// <summary>Creates the start info for the 3D worker given its arguments. Set by the host app.</summary>
    public static Func<string, ProcessStartInfo> LaunchFactory { get; set; } = DefaultLaunch;

    public Component Component => Component.Gpu;
    public const int InternalWidth = 1920, InternalHeight = 1080;

    public static ProcessStartInfo DefaultLaunch(string args)
    {
        var overridePath = Environment.GetEnvironmentVariable("BENCHFORGE_GPU_WORKER");
        if (!string.IsNullOrEmpty(overridePath))
            return overridePath.EndsWith(".dll") ? new ProcessStartInfo("dotnet", $"\"{overridePath}\" {args}") : new ProcessStartInfo(overridePath, args);
        var baseDir = AppContext.BaseDirectory;
        var exe = Path.Combine(baseDir, OperatingSystem.IsWindows() ? "BenchForge.Gpu.exe" : "BenchForge.Gpu");
        if (File.Exists(exe)) return new ProcessStartInfo(exe, args);
        var dll = Path.Combine(baseDir, "BenchForge.Gpu.dll");
        if (File.Exists(dll)) return new ProcessStartInfo("dotnet", $"\"{dll}\" {args}");
        // Same executable with a worker switch (single-file app).
        return new ProcessStartInfo(Environment.ProcessPath ?? "BenchForge", "--gpu-worker " + args);
    }

    /// <summary>Which APIs to run for a setting: one, or every API this OS can have ("all").</summary>
    public static IReadOnlyList<string> ApisFor(string? setting, StepKind kind)
    {
        var all = OperatingSystem.IsWindows() ? GpuApis.All : new[] { GpuApis.Vulkan, GpuApis.OpenGL };
        if (string.IsNullOrEmpty(setting)) return new[] { GpuApis.OpenGL };
        if (setting != "all") return new[] { setting };
        // Stress runs on one API (the most game-like one available); benchmarks compare them all.
        return kind == StepKind.Benchmark ? all : new[] { all[0] };
    }

    private sealed class ApiRun
    {
        public string Api = "";
        public readonly List<GpuMessage> Scenes = new();
        public GpuMessage? Info, Pcie;
        public long VramErrors, VramChecked;
        public bool Done, Hung, Unavailable;
        public string? FailureReason;
    }

    public async Task<ModuleResult> RunAsync(StepContext ctx)
    {
        var result = new ModuleResult { Component = Component.Gpu, Kind = ctx.Kind };
        var apis = ApisFor(ctx.Settings.GpuApi, ctx.Kind);
        bool multi = apis.Count > 1;
        result.Info["Internal resolution"] = $"{InternalWidth}×{InternalHeight} (fixed)";
        var runs = new List<ApiRun>();
        double total = Math.Max(5, ctx.DurationSeconds - 3 * apis.Count);
        var start = DateTime.Now;
        for (int i = 0; i < apis.Count && !ctx.Token.IsCancellationRequested; i++)
        {
            // Split what's left evenly, so an API that is skipped quickly gives its time to the others.
            double left = Math.Max(5, total - (DateTime.Now - start).TotalSeconds);
            double seconds = Math.Max(5, left / (apis.Count - i));
            var run = await RunApi(ctx, apis[i], seconds, i, apis.Count);
            runs.Add(run);
        }

        // Info and failures
        var ok = runs.Where(r => !r.Unavailable).ToList();
        result.Info["Graphics API"] = string.Join(", ", runs.Select(r => GpuApis.Display(r.Api) + (r.Unavailable ? " (not available)" : r.FailureReason != null ? " (failed)" : "")));
        var first = ok.FirstOrDefault(r => r.Info != null);
        if (first?.Info != null)
        {
            result.Info["Renderer"] = first.Info.Renderer ?? "";
            result.Info["Vendor"] = first.Info.Vendor ?? "";
        }
        foreach (var r in ok.Where(r => r.Info != null)) result.Info[GpuApis.Display(r.Api)] = r.Info!.Version ?? "";
        foreach (var r in runs.Where(r => r.Unavailable && r.FailureReason != null)) result.Info[GpuApis.Display(r.Api) + " note"] = r.FailureReason!;

        foreach (var r in runs.Where(r => !r.Unavailable && r.FailureReason != null))
        {
            if (!result.Failed) { result.Failed = true; result.FailureReason = r.FailureReason; }
            ctx.Error(new TestError { Test = $"3D test ({GpuApis.Display(r.Api)})", Message = r.FailureReason! });
        }
        if (ok.Count == 0 && !ctx.Token.IsCancellationRequested)
        {
            result.Failed = true;
            result.FailureReason ??= "No graphics API could run the 3D test: " + string.Join("; ", runs.Select(r => r.FailureReason).Where(x => x != null));
            ctx.Error(new TestError { Test = "3D test", Message = result.FailureReason });
        }

        long vramErrors = runs.Sum(r => r.VramErrors), vramChecked = runs.Sum(r => r.VramChecked);
        result.ErrorCount = (int)vramErrors + runs.Count(r => !r.Unavailable && r.FailureReason != null);
        if (vramChecked > 0) result.Info["VRAM verified"] = $"{vramChecked / 1e9:0.0} GB, {vramErrors} errors";

        // Scores: per API, then the module score is the best API's (games use whichever runs best).
        var apiScores = new List<(string Api, double Score)>();
        foreach (var r in ok)
        {
            var refs = ScoreReferences.GpuFor(r.Api);
            var scores = new List<double>();
            foreach (var sc in r.Scenes)
            {
                if (sc.Scene == null) continue;
                double score = refs.TryGetValue(sc.Scene, out var refFps) ? Score.Linear(sc.AvgFps, refFps) : 0;
                scores.Add(score);
                result.SubScores.Add(new SubScore
                {
                    Name = multi ? $"{sc.Scene} ({GpuApis.Display(r.Api)})" : sc.Scene,
                    Group = ctx.Kind == StepKind.Benchmark ? (multi ? $"3D scenes — {GpuApis.Display(r.Api)}" : "3D scenes") : "Stress",
                    Value = Math.Round(sc.AvgFps, 1), Unit = "FPS", Score = Math.Round(score), Low1Fps = Math.Round(sc.Low1Fps, 1),
                    Detail = $"1% low {sc.Low1Fps:0.0} FPS · 99th percentile frame {sc.P99Ms:0.0} ms · {sc.Frames} frames",
                });
            }
            if (ctx.Kind == StepKind.Benchmark && scores.Count(s => s > 0) == GpuScenes.Benchmark.Length && r.FailureReason == null)
            {
                double s = Math.Round(Score.GeoMean(scores));
                apiScores.Add((r.Api, s));
                if (multi) result.SubScores.Add(new SubScore { Name = $"{GpuApis.Display(r.Api)} score", Group = "Graphics APIs", Value = s, Unit = "pts", Score = s });
            }
        }
        if (apiScores.Count > 0 && !result.Failed)
        {
            var best = apiScores.MaxBy(x => x.Score);
            result.Score = best.Score;
            if (multi) result.Info["Best graphics API"] = $"{GpuApis.Display(best.Api)} ({best.Score:0} points)";
        }

        var pcie = ok.Select(r => r.Pcie).FirstOrDefault(p => p != null);
        if (pcie != null)
        {
            result.SubScores.Add(new SubScore { Name = "PCIe upload (PC → graphics card)", Group = "PCIe", Value = pcie.UploadGBs, Unit = "GB/s" });
            result.SubScores.Add(new SubScore { Name = "PCIe download (graphics card → PC)", Group = "PCIe", Value = pcie.DownloadGBs, Unit = "GB/s" });
        }
        ctx.Progress("Done", 1);
        return result;
    }

    private async Task<ApiRun> RunApi(StepContext ctx, string api, double seconds, int index, int count)
    {
        var run = new ApiRun { Api = api };
        var s = ctx.Settings;
        // The internal render resolution is fixed so scores are comparable on every PC, whatever the window or monitor size.
        var args = string.Create(CultureInfo.InvariantCulture,
            $"--api {api} --mode {(ctx.Kind == StepKind.Benchmark ? "bench" : "stress")} --duration {seconds:0.0} --width {InternalWidth} --height {InternalHeight}{(s.GpuFullscreen ? " --fullscreen" : "")}");
        var psi = LaunchFactory(args);
        psi.UseShellExecute = false;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardInput = true;
        psi.CreateNoWindow = true;

        var stderr = new List<string>();
        var errors = new List<string>();
        var lastMessage = DateTime.Now;
        string? currentScene = null;
        bool started = false;
        string apiName = GpuApis.Display(api);

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
                    run.Info = m;
                    ctx.Log($"3D test ({apiName}) running on {m.Renderer} ({m.Version})");
                    break;
                case "progress":
                    started = true;
                    currentScene = m.Scene;
                    double within = m.Count > 0 ? (m.Index + m.Fraction) / m.Count : m.Fraction;
                    double overall = ctx.Kind == StepKind.Stress ? ctx.Elapsed.TotalSeconds / ctx.DurationSeconds : (index + within) / count;
                    ctx.Progress(count > 1 ? $"{apiName}: {m.Scene} — {m.Fps:0} FPS" : $"{m.Scene} — {m.Fps:0} FPS", overall);
                    ctx.Metric("gpu.fps", "3D test FPS", Math.Round(m.Fps, 1), "FPS");
                    break;
                case "scene":
                    lock (run.Scenes) run.Scenes.Add(m);
                    ctx.Log($"{apiName} — {m.Scene}: {m.AvgFps:0.0} FPS average, 1% low {m.Low1Fps:0.0} FPS");
                    break;
                case "pcie":
                    run.Pcie = m;
                    ctx.Log($"PCIe transfer: {m.UploadGBs:0.0} GB/s to the graphics card, {m.DownloadGBs:0.0} GB/s back");
                    break;
                case "vram":
                    run.VramChecked = m.Checked;
                    if (m.Errors > run.VramErrors)
                        ctx.Error(new TestError { Test = GpuScenes.Vram, Message = $"{m.Errors - run.VramErrors} video memory errors detected ({apiName})", Detail = m.Message });
                    run.VramErrors = m.Errors;
                    break;
                case "error":
                    lock (errors) errors.Add(m.Message ?? "GPU error");
                    break;
                case "log":
                    ctx.Log(m.Message ?? "");
                    break;
                case "done":
                    run.Done = true;
                    break;
            }
        };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (stderr) { if (stderr.Count < 50) stderr.Add(e.Data); } };

        try { proc.Start(); }
        catch (Exception ex)
        {
            run.FailureReason = "Could not start the 3D test: " + ex.Message;
            return run;
        }
        try { proc.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        // PSU spike test: switch the GPU on and off together with the CPU (same schedule, same clock).
        var spikeTask = ctx.Step.Variant == StepVariants.PsuSpike ? Task.Run(async () =>
        {
            bool? sent = null;
            while (!proc.HasExited && !ctx.Token.IsCancellationRequested)
            {
                bool on = PsuSpikeSchedule.IsOn(ctx.Elapsed.TotalSeconds);
                if (sent != on)
                {
                    try { proc.StandardInput.WriteLine(on ? "resume" : "pause"); proc.StandardInput.Flush(); } catch { break; }
                    sent = on;
                }
                await Task.Delay(10);
            }
        }) : Task.CompletedTask;

        while (!proc.HasExited)
        {
            try { await Task.Delay(250, ctx.Token); } catch (OperationCanceledException) { break; }
            if ((DateTime.Now - lastMessage).TotalSeconds > 20) { run.Hung = true; break; }
        }
        if (!proc.HasExited)
        {
            try { proc.StandardInput.WriteLine("stop"); proc.StandardInput.Flush(); } catch { }
            if (!proc.WaitForExit(4000)) { try { proc.Kill(true); } catch { } }
        }
        try { proc.WaitForExit(2000); } catch { }
        try { await spikeTask; } catch { }
        int exitCode = 0;
        try { exitCode = proc.ExitCode; } catch { }

        string? firstError; lock (errors) firstError = errors.FirstOrDefault();
        if (run.Hung)
            run.FailureReason = $"The 3D test ({apiName}) stopped responding during '{currentScene}'. This usually means the GPU driver hung or reset (TDR).";
        else if (!run.Done && !ctx.Token.IsCancellationRequested)
        {
            // Exit codes 2/3 before anything was drawn: this API isn't available here (no driver support) — not a crash.
            if (!started && exitCode is 2 or 3)
            {
                run.Unavailable = true;
                run.FailureReason = firstError ?? $"{apiName} could not start on this PC.";
                ctx.Log($"{apiName} is not available: {run.FailureReason}", Severity.Warning);
                // When only this API was asked for, that is a failure of the step.
                if (count == 1) { run.Unavailable = false; run.FailureReason = $"The 3D test could not start with {apiName}: {run.FailureReason}"; }
            }
            else
            {
                run.FailureReason = $"The 3D test process ({apiName}) ended unexpectedly (exit code {exitCode}) during '{currentScene ?? "startup"}'. " +
                                    "This usually means the graphics driver crashed or the GPU is unstable." + (firstError != null ? " " + firstError : "");
            }
        }
        lock (stderr) if (stderr.Count > 0 && run.FailureReason != null) run.FailureReason += " | " + string.Join(" | ", stderr.Take(3));
        return run;
    }
}
