using System.Globalization;
using BenchForge.Core.Engine;
using BenchForge.Core.Gpu;
using BenchForge.Core.Model;
using BenchForge.Core.Persistence;
using BenchForge.Core.Reporting;
using BenchForge.Core.Scoring;

// Headless runner: handy for automation and for testing the engine without the Windows UI.
//   benchforge-cli quick                     quick benchmark
//   benchforge-cli bench cpu|ram|disk|gpu    one benchmark
//   benchforge-cli stress cpu,ram 60         stress parts for N seconds
//   --out <folder>                           save .bfresult + HTML report
var argList = args.ToList();
string? outDir = null;
int oi = argList.IndexOf("--out");
if (oi >= 0 && oi + 1 < argList.Count) { outDir = argList[oi + 1]; argList.RemoveRange(oi, 2); }
if (argList.Count == 0) { Console.WriteLine("usage: benchforge-cli quick | bench <part> | stress <parts> <seconds> [--out dir]"); return 1; }
if (outDir != null) DataPaths.Override(outDir);

if (argList[0] == "export-ai")
{
    // benchforge-cli export-ai <folder with .bfresult files> <output.md>
    var files = Directory.GetFiles(argList[1], "*.bfresult", SearchOption.AllDirectories);
    var runs = files.Select(ResultStore.Load).Where(r => r != null).Select(r => r!).ToList();
    var md = BenchForge.Core.Ai.AiExport.BuildMarkdown(runs);
    File.WriteAllText(argList[2], md);
    Console.WriteLine($"Wrote {argList[2]} ({md.Length / 1024} KB, {runs.Count} runs)");
    return 0;
}

if (argList[0] == "recover")
{
    foreach (var rec in SessionJournal.FindUnfinished())
    {
        var crashed = BenchForge.Core.Crash.CrashAnalyzer.FromJournal(rec, null, _ => new());
        Console.WriteLine($"Unfinished session {rec.Header.RunId} '{rec.Header.Name}' last alive {rec.LastAlive:HH:mm:ss}");
        Console.WriteLine($"  {crashed.Crash!.Kind}: {crashed.Crash.Summary}");
        foreach (var e in crashed.Crash.Explanation) Console.WriteLine("  - " + e);
        Console.WriteLine($"  tail samples: {crashed.Crash.Tail.Count}, peak metrics: {string.Join(", ", crashed.Crash.PeakMetrics.Select(kv => $"{kv.Key}={kv.Value:0.#}"))}");
        Console.WriteLine($"  completed steps: {crashed.Steps.Count}");
        foreach (var r in crashed.Recommendations) Console.WriteLine($"  FIX [{r.Severity}] {r.Title}");
        new ResultStore().Save(crashed);
        SessionJournal.Discard(rec);
    }
    return 0;
}

var settings = new TestSettings { MemoryPercent = 30, StorageFileMB = 256 };
static Component Parse(string s) => s.ToLowerInvariant() switch
{
    "cpu" => Component.Cpu, "ram" or "memory" => Component.Memory, "disk" or "storage" => Component.Storage, "gpu" => Component.Gpu,
    _ => throw new ArgumentException("unknown part " + s),
};
TestPlan plan = argList[0] switch
{
    "quick" => PlanPresets.QuickBenchmark(settings),
    "bench" => new TestPlan { Name = "bench", Settings = settings, Steps = { PlanPresets.QuickBenchmark().Steps.First(s => s.Components.Contains(Parse(argList[1]))) } },
    "stress" => new TestPlan { Name = "stress", Settings = settings, Steps = { new TestStep("Stress", StepKind.Stress, double.Parse(argList[2], CultureInfo.InvariantCulture), argList[1].Split(',').Select(Parse).ToArray()) } },
    _ => throw new ArgumentException("unknown command"),
};

var runner = new PlanRunner(plan, new ModuleFactory(), new BasicSensorProvider(), null, useJournal: outDir != null);
double lastPrint = -1;
runner.ProgressChanged += p =>
{
    if (p.Elapsed.TotalSeconds - lastPrint < 5) return;
    lastPrint = p.Elapsed.TotalSeconds;
    Console.WriteLine($"[{p.OverallFraction:P0}] {p.StepName} — {TimeFormat.Long(p.Remaining)} left · {string.Join("; ", p.Phases.Select(x => $"{x.Key}: {x.Value.Phase}"))}");
};
runner.Logged += l => Console.WriteLine($"  {l.Time:HH:mm:ss} {l.Message}");
runner.ErrorReported += e => Console.WriteLine($"  ERROR {e.Component} {e.Test}: {e.Message} {e.Detail}");
var result = await runner.RunAsync();

Console.WriteLine();
Console.WriteLine($"Status: {result.Status}  Overall: {(result.OverallScore is double o ? Score.Format(o) : "-")}  Errors: {result.TotalErrorCount}");
foreach (var m in result.AllModules)
{
    Console.WriteLine($"== {m.Component} {m.Kind} score {m.Score?.ToString("0") ?? "-"} {(m.Failed ? "FAILED: " + m.FailureReason : "")}");
    foreach (var s in m.SubScores) Console.WriteLine($"   {s.Group,-14} {s.Name,-36} {s.Value,12:#,0.##} {s.Unit,-16} {s.Score,7:0}");
    foreach (var kv in m.Info) Console.WriteLine($"   - {kv.Key}: {kv.Value}");
}
foreach (var f in result.Findings) Console.WriteLine($"[{f.Severity}] {f.Title} {f.Detail}");
foreach (var r in result.Recommendations) Console.WriteLine($"FIX [{r.Severity}] {r.Title}");
if (outDir != null)
{
    var path = new ResultStore().Save(result);
    File.WriteAllText(Path.ChangeExtension(path, ".html"), HtmlReport.Build(result));
    Console.WriteLine("Saved " + path);
}
return 0;

/// <summary>Minimal cross-platform sensors: CPU load from /proc/stat and temperatures from /sys/class/thermal.</summary>
sealed class BasicSensorProvider : ISensorProvider
{
    private long _idle, _total;
    public string? Warning => null;
    public SensorSnapshot Read()
    {
        var snap = new SensorSnapshot();
        try
        {
            if (File.Exists("/proc/stat"))
            {
                var f = File.ReadLines("/proc/stat").First().Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(long.Parse).ToArray();
                long idle = f[3] + f[4], total = f.Sum();
                if (_total > 0 && total > _total)
                    snap.Readings.Add(new SensorReading("/cpu/load", "CPU", HwType.Cpu, SensorKind.Load, "CPU Total", Math.Round(100.0 * (1 - (idle - _idle) / (double)(total - _total)), 1), "%"));
                _idle = idle; _total = total;
            }
            if (Directory.Exists("/sys/class/thermal"))
                foreach (var z in Directory.GetDirectories("/sys/class/thermal", "thermal_zone*"))
                    if (double.TryParse(File.ReadAllText(Path.Combine(z, "temp")).Trim(), out var mc))
                        snap.Readings.Add(new SensorReading(z, "CPU", HwType.Cpu, SensorKind.Temperature, "Package", mc / 1000.0, "°C"));
            var (tot, avail) = BenchForge.Core.Platform.MemoryStatus.Get();
            snap.Readings.Add(new SensorReading("/ram/load", "Memory", HwType.Memory, SensorKind.Load, "Memory", Math.Round(100.0 * (1 - avail / (double)tot), 1), "%"));
        }
        catch { }
        return snap;
    }
    public void Dispose() { }
}
