using System.Globalization;
using System.Text;
using BenchForge.Core.Analysis;
using BenchForge.Core.Crash;
using BenchForge.Core.Model;

namespace BenchForge.Core.Ai;

/// <summary>
/// Builds one self-explaining Markdown file with everything an AI needs to diagnose the PC. Upload it to Claude.ai
/// (or any AI chat) instead of giving BenchForge an API key.
/// </summary>
public static class AiExport
{
    public const int MaxBytes = 900_000;
    private const int MaxSensorRows = 300;

    private static readonly string[] SensorColumns =
    {
        KeyMetrics.CpuTemp, KeyMetrics.CpuLoad, KeyMetrics.CpuClock, KeyMetrics.CpuPower, KeyMetrics.CpuVcore,
        KeyMetrics.GpuTemp, KeyMetrics.GpuHotspot, KeyMetrics.GpuLoad, KeyMetrics.GpuClock, KeyMetrics.GpuPower,
        KeyMetrics.RamLoad, KeyMetrics.DiskTemp, KeyMetrics.Rail12V,
    };

    public static string FileName(DateTime now) => $"BenchForge-for-AI-{Environment.MachineName}-{now:yyyy-MM-dd-HHmm}.md";

    public static string StarterPrompt =>
        "I've attached a diagnostics file from BenchForge (a PC benchmark / stress test / crash analysis tool). " +
        "Please read all of it and tell me what is most likely wrong with this PC, what evidence points to it, and what I should do, in order.";

    public static string BuildMarkdown(IReadOnlyList<RunResult> runs, IReadOnlyList<SystemCrash>? crashes = null, IReadOnlyList<EventEntry>? events = null,
        HardwareInventory? hardware = null, IReadOnlyList<Recommendation>? systemChecks = null, string? question = null, DateTime? now = null,
        DiagnosticsExtras? extras = null)
    {
        var at = now ?? DateTime.Now;
        hardware ??= runs.Select(r => r.Hardware).FirstOrDefault(h => h != null);
        var head = new StringBuilder();
        head.AppendLine("# BenchForge diagnostics for AI analysis");
        head.AppendLine();
        head.AppendLine($"Exported {at:yyyy-MM-dd HH:mm} from PC \"{hardware?.ComputerName ?? Environment.MachineName}\" · {runs.Count} test run(s) · {crashes?.Count ?? 0} Windows crash(es) · {events?.Count ?? 0} event(s)");
        head.AppendLine();
        head.AppendLine("## Instructions for the AI");
        head.AppendLine();
        head.AppendLine(AiAnalyzer.SystemPrompt);
        head.AppendLine();
        head.AppendLine("How to read this file: scores are linear (1000 = reference PC, 2000 = twice as fast). \"Errors\" in CPU/RAM/VRAM/drive tests mean the hardware returned WRONG results (instability), not software bugs. " +
                        "Sensor tables are CSV; `t` is seconds since the run started; empty cells = sensor not available. Crash sections were built from the last heartbeat BenchForge wrote before the PC went down, plus Windows event logs and crash dumps.");
        head.AppendLine();
        head.AppendLine("**Question:** " + (string.IsNullOrWhiteSpace(question)
            ? "What is wrong with this PC, how sure are you, and what should be done (in order)? If several runs/crashes are included, find what they have in common."
            : question));
        head.AppendLine();

        var sections = new List<(string Text, int Priority)>();

        if (hardware != null) sections.Add((HardwareSection(hardware), 0));

        if (systemChecks is { Count: > 0 })
        {
            var sb = new StringBuilder("## Problems detected without a test\n\n");
            foreach (var r in systemChecks) sb.AppendLine($"- **[{r.Severity}] {r.Title}** — {r.Detail}");
            sections.Add((sb.AppendLine().ToString(), 1));
        }

        // Offline pattern analysis first so the AI sees BenchForge's own conclusion.
        var cases = runs.Where(r => r.IsFailure).Select(FailureCase.FromRun)
            .Concat(crashes?.Select(FailureCase.FromSystemCrash) ?? Enumerable.Empty<FailureCase>()).ToList();
        if (cases.Count > 0)
        {
            var rep = PatternAnalyzer.Analyze(cases, runs.Where(r => !r.IsFailure).ToList());
            var sb = new StringBuilder("## BenchForge's own analysis (offline, rule-based)\n\n");
            sb.AppendLine($"**{rep.Verdict}**").AppendLine();
            foreach (var f in rep.Findings) sb.AppendLine($"- [{f.Severity}, {f.Confidence:P0}] {f.Title}: {f.Detail}");
            if (rep.NextSteps.Count > 0) { sb.AppendLine().AppendLine("Suggested next steps:"); foreach (var s in rep.NextSteps) sb.AppendLine($"1. {s}"); }
            sections.Add((sb.AppendLine().ToString(), 1));
        }

        int i = 0;
        foreach (var r in runs.OrderByDescending(r => r.StartTime))
            sections.Add((RunSection(r, ++i), r.IsFailure ? 2 : 3));

        if (crashes is { Count: > 0 })
        {
            var sb = new StringBuilder("## Crashes recorded by Windows\n\n");
            foreach (var c in crashes.OrderByDescending(c => c.Time)) sb.Append(CrashText(c.Info, $"### {c.Time:yyyy-MM-dd HH:mm} — {c.Info.Summary}"));
            sections.Add((sb.ToString(), 2));
        }

        if (extras != null)
        {
            if (extras.Reliability.Count > 0)
            {
                var sb = new StringBuilder("## Windows Reliability Monitor (last 30 days)\n\n");
                foreach (var e in extras.Reliability.OrderByDescending(e => e.Time).Take(150))
                    sb.AppendLine($"- `{e.Time:yyyy-MM-dd HH:mm}` **{e.Source}** {e.Product} (ID {e.EventId}): {Clean(e.Message, 400)}");
                sections.Add((sb.AppendLine().ToString(), 3));
            }
            if (extras.Drivers.Count > 0)
            {
                var sb = new StringBuilder("## Installed drivers (graphics, chipset, storage, network, audio, USB …)\n\n| Class | Device | Provider | Version | Date |\n|---|---|---|---|---|\n");
                foreach (var d in extras.Drivers.OrderBy(d => d.DeviceClass).ThenBy(d => d.Device))
                    sb.AppendLine($"| {d.DeviceClass} | {Cell(d.Device)} | {Cell(d.Provider)} | {d.Version} | {d.Date:yyyy-MM-dd} |");
                sections.Add((sb.AppendLine().ToString(), 3));
            }
            if (extras.RecentErrors.Count > 0)
            {
                var sb = new StringBuilder("## All errors & critical events in Windows logs (last 7 days)\n\n");
                foreach (var e in extras.RecentErrors.OrderByDescending(e => e.Time).Take(250))
                    sb.AppendLine($"- `{e.Time:yyyy-MM-dd HH:mm:ss}` {e.Log} · **{e.Provider}** ID {e.Id} ({e.LevelName}): {Clean(e.Message, 400)}");
                sections.Add((sb.AppendLine().ToString(), 5));
            }
            if (extras.AppLogTail.Count > 0 || extras.Notes.Count > 0)
            {
                var sb = new StringBuilder("## BenchForge app log (last lines)\n\n```text\n");
                foreach (var l in extras.Notes) sb.AppendLine("NOTE: " + l);
                foreach (var l in extras.AppLogTail.TakeLast(150)) sb.AppendLine(l);
                sections.Add((sb.AppendLine("```").AppendLine().ToString(), 6));
            }
        }

        if (events is { Count: > 0 })
        {
            var sb = new StringBuilder("## Windows events\n\n");
            foreach (var e in events.OrderByDescending(e => e.Time).Take(150))
                sb.AppendLine($"- `{e.Time:yyyy-MM-dd HH:mm:ss}` **{e.Provider}** ID {e.Id} ({e.LevelName}): {Clean(e.Message, 600)}");
            sections.Add((sb.AppendLine().ToString(), 4));
        }

        // Size cap: drop the least important sections (highest priority number, oldest last) until it fits.
        var body = sections.ToList();
        string Compose() => head + string.Concat(body.Select(s => s.Text)) + "\n---\nEnd of BenchForge export.\n";
        var text = Compose();
        while (Encoding.UTF8.GetByteCount(text) > MaxBytes && body.Count > 1)
        {
            var drop = body.Select((s, idx) => (s, idx)).OrderByDescending(x => x.s.Priority).ThenByDescending(x => x.idx).First();
            body.RemoveAt(drop.idx);
            text = Compose();
        }
        if (Encoding.UTF8.GetByteCount(text) > MaxBytes) text = text[..(MaxBytes / 2)] + "\n…(truncated)\n";
        return text;
    }

    private static string HardwareSection(HardwareInventory hw)
    {
        var sb = new StringBuilder("## This PC\n\n");
        sb.AppendLine($"- **CPU:** {hw.Cpu.Name} — {hw.Cpu.Cores} cores / {hw.Cpu.Threads} threads, max {hw.Cpu.MaxClockMHz} MHz, {hw.Cpu.InstructionSets}");
        foreach (var g in hw.Gpus) sb.AppendLine($"- **GPU:** {g.Name} — {g.VramGB:0.#} GB, driver {g.DriverVersion} ({g.DriverDate:yyyy-MM-dd}), {g.Resolution}");
        sb.AppendLine($"- **RAM:** {hw.TotalMemoryGB:0.#} GB in {hw.MemoryModules.Count} module(s)");
        foreach (var m in hw.MemoryModules) sb.AppendLine($"  - {m.Slot}: {m.CapacityGB:0} GB {m.Type} {m.Manufacturer} {m.PartNumber}, running {m.ConfiguredSpeedMTs} MT/s (rated {m.SpeedMTs})");
        foreach (var d in hw.Disks)
            sb.AppendLine($"- **Drive:** {d.Model} — {d.SizeGB:0} GB {d.BusType} {d.MediaType}, health {d.Health}" +
                          (d.TemperatureC != null ? $", {d.TemperatureC:0} °C" : "") + (d.WearPercent != null ? $", wear {d.WearPercent:0}%" : "") +
                          (d.PowerOnHours != null ? $", {d.PowerOnHours} h on" : "") + (d.ReadErrors is > 0 ? $", {d.ReadErrors} read errors" : "") +
                          (d.Volumes.Count > 0 ? $", {string.Join(" ", d.Volumes)}" : "") + (d.FreePercent != null ? $" ({d.FreePercent:0}% free)" : ""));
        foreach (var d in hw.Disks.Where(d => d.Smart.Count > 0 || d.Nvme != null || d.Misaligned == true))
        {
            sb.AppendLine($"- **Drive health — {d.Model}:**");
            if (d.Nvme is NvmeHealthInfo n)
                sb.AppendLine($"  - NVMe: critical warning {Storage.SmartParser.DescribeCriticalWarning(n.CriticalWarning)}; {n.PercentageUsed}% life used; spare {n.AvailableSparePercent}% (threshold {n.SpareThresholdPercent}%); media errors {n.MediaErrors}; unsafe shutdowns {n.UnsafeShutdowns}; {n.PowerOnHours} h on; {n.PowerCycles} power cycles; {n.DataWrittenTB:0.##} TB written / {n.DataReadTB:0.##} TB read; {n.TemperatureC} °C");
            if (d.Smart.Count > 0)
                sb.AppendLine("  - SMART: " + string.Join("; ", d.Smart.Select(a => $"{a.Id} {a.Name} = {a.Raw} (value {a.Current}/{a.Worst}{(a.Threshold is int t ? $", threshold {t}" : "")}{(a.Failing ? ", FAILING" : "")})")));
            if (d.Misaligned == true) sb.AppendLine("  - Partition NOT aligned to 4 KB");
        }
        foreach (var l in hw.PcieLinks) sb.AppendLine($"- **PCIe link — {l.Device} ({l.Kind}):** {l}{(l.Degraded ? " ← running below maximum" : "")}");
        if (hw.TrimEnabled is bool trim) sb.AppendLine($"- **TRIM:** {(trim ? "on" : "OFF")}");
        sb.AppendLine($"- **Motherboard:** {hw.Motherboard}, BIOS {hw.BiosVersion} ({hw.BiosDate:yyyy-MM-dd})");
        sb.AppendLine($"- **Windows:** {hw.OsName} {hw.OsVersion}, last boot {hw.LastBoot:yyyy-MM-dd HH:mm}, power plan \"{hw.PowerPlan}\"");
        if (hw.IsLaptop || hw.HasBattery)
            sb.AppendLine($"- **Laptop:** {(hw.IsLaptop ? "yes" : "no")} ({hw.ChassisType}); battery {(hw.HasBattery ? $"{(hw.BatteryWearPercent is double w ? $"health {100 - w:0}% ({hw.BatteryFullChargeWh:0.#} of {hw.BatteryDesignCapacityWh:0.#} Wh)" : "present")}" : "none")}; charger {(hw.OnBattery switch { true => "NOT connected", false => "connected", _ => "unknown" })}");
        sb.AppendLine($"- **Settings:** crash dumps {DumpText(hw.CrashDumpSetting)}, Fast Startup {(hw.FastStartupEnabled is bool f ? (f ? "on" : "off") : "unknown")}, GPU TDR delay {(hw.TdrDelaySeconds is int t ? t + " s" : "default (2 s)")}");
        foreach (var n in hw.Notes) sb.AppendLine($"- Note: {n}");
        return sb.AppendLine().ToString();
    }

    private static string DumpText(int? v) => v switch
    {
        0 => "OFF (no dumps written)", 1 => "complete", 2 => "kernel", 3 => "small (minidump)", 7 => "automatic", null => "unknown", _ => v.ToString()!,
    };

    private static string RunSection(RunResult r, int index)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## Run {index}: {r.DisplayName} — {r.StartTime:yyyy-MM-dd HH:mm} — {Reporting.HtmlReport.StatusText(r.Status)}");
        sb.AppendLine();
        sb.AppendLine($"- PC: {r.ImportedFrom ?? r.MachineName}; duration {TimeFormat.Short(r.Duration)}; errors found: {r.TotalErrorCount}");
        if (r.AbortReason != null) sb.AppendLine($"- Stopped early: {r.AbortReason}");
        if (r.OverallScore != null || r.ComponentScores.Count > 0)
            sb.AppendLine($"- Scores: overall {r.OverallScore?.ToString("0") ?? "n/a"}; " + string.Join(", ", r.ComponentScores.Select(kv => $"{kv.Key.Display()} {kv.Value:0}")));
        var settings = r.Plan.Settings;
        sb.AppendLine($"- Plan: {string.Join(" → ", r.Plan.Steps.Take(12).Select(s => $"{s.Name} ({TimeFormat.Short(TimeSpan.FromSeconds(s.DurationSeconds))})"))}{(r.Plan.Steps.Count > 12 ? " …" : "")}; {(settings.LaptopRules ? "LAPTOP rules (CPU at its thermal limit is normal), " : "")}CPU stress mode {settings.CpuStressMode}, safety limits CPU {settings.CpuTempLimit:0} °C / GPU {settings.GpuTempLimit:0} °C");
        sb.AppendLine();

        if (r.Crash is CrashInfo c) sb.Append(CrashText(c, "### What happened (crash)"));

        sb.AppendLine("### Test results").AppendLine();
        foreach (var s in r.Steps)
        {
            sb.AppendLine($"**{s.Name}** ({s.Kind}, {string.Join(" + ", s.Components.Select(x => x.Display()))}) — {s.Status}");
            foreach (var m in s.Modules)
            {
                sb.AppendLine($"- {m.Component.Display()}{(m.Score is double sc ? $" score {sc:0}" : "")}{(m.ErrorCount > 0 ? $", **{m.ErrorCount} errors**" : "")}{(m.Failed ? $", FAILED: {m.FailureReason}" : "")}");
                foreach (var x in m.SubScores) sb.AppendLine($"  - {x.Group} · {x.Name}: {x.Value.ToString("#,0.##", CultureInfo.InvariantCulture)} {x.Unit}{(x.Score > 0 ? $" ({x.Score:0} pts)" : "")}{(x.Detail != null ? " — " + x.Detail : "")}");
                foreach (var kv in m.Info) sb.AppendLine($"  - {kv.Key}: {kv.Value}");
                foreach (var ch in m.Charts)
                {
                    sb.AppendLine($"  - Chart \"{ch.Title}\"{(ch.Note != null ? " — " + ch.Note : "")}:").AppendLine().AppendLine("```csv").AppendLine($"{ch.XLabel},{ch.YLabel}");
                    int step = Math.Max(1, ch.Points.Count / 200);
                    for (int i = 0; i < ch.Points.Count; i += step) sb.AppendLine($"{ch.Points[i][0].ToString(CultureInfo.InvariantCulture)},{ch.Points[i][1].ToString(CultureInfo.InvariantCulture)}");
                    sb.AppendLine("```").AppendLine();
                }
            }
            sb.AppendLine();
        }

        if (r.KeyStats.Count > 0)
        {
            sb.AppendLine("### Sensor summary (min / avg / max)").AppendLine();
            foreach (var (k, st) in r.KeyStats) sb.AppendLine($"- {KeyMetrics.Label(k)}: {st.Min:0.##} / {st.Avg:0.##} / {st.Max:0.##} {KeyMetrics.Unit(k)}");
            sb.AppendLine();
        }

        var allStats = r.SensorStats.Where(x => x.HwType != HwType.Test && !x.Id.StartsWith("key/")).ToList();
        if (allStats.Count > 0)
        {
            sb.AppendLine($"### Every sensor during this run ({allStats.Count} sensors, min / avg / max)").AppendLine();
            sb.AppendLine("| Hardware | Sensor | Type | Min | Avg | Max | Unit |").AppendLine("|---|---|---|---|---|---|---|");
            foreach (var x in allStats.OrderBy(x => x.HwType).ThenBy(x => x.Hardware).ThenBy(x => x.Kind).ThenBy(x => x.Name))
                sb.AppendLine($"| {Cell(x.Hardware)} | {Cell(x.Name)} | {x.Kind} | {x.Min:0.##} | {x.Avg:0.##} | {x.Max:0.##} | {x.Unit} |");
            sb.AppendLine();
        }

        // A crashed run's timeline is the crash tail, which is already printed above.
        bool timelineIsTail = r.Crash != null && r.Crash.Tail.Count > 0 && r.Timeline.Samples.Count <= r.Crash.Tail.Count;
        var table = timelineIsTail ? null : SensorTable(r.Timeline, MaxSensorRows);
        if (table != null) sb.AppendLine("### Sensor history (CSV, downsampled)").AppendLine().AppendLine("```csv").Append(table).AppendLine("```").AppendLine();

        if (r.Errors.Count > 0)
        {
            sb.AppendLine("### Errors").AppendLine();
            foreach (var e in r.Errors.Take(150)) sb.AppendLine($"- `{e.Time:HH:mm:ss}` {e.Component.Display()} · {e.Test}{(e.Thread != null ? $" · thread {e.Thread}" : "")}: {e.Message} {e.Detail}");
            if (r.TotalErrorCount > 150) sb.AppendLine($"- … {r.TotalErrorCount - 150} more");
            sb.AppendLine();
        }
        if (r.Findings.Count > 0)
        {
            sb.AppendLine("### BenchForge findings").AppendLine();
            foreach (var f in r.Findings) sb.AppendLine($"- [{f.Severity}] {f.Title}{(f.Detail.Length > 0 ? " — " + f.Detail : "")}");
            sb.AppendLine();
        }
        if (r.Recommendations.Count > 0)
        {
            sb.AppendLine("### BenchForge recommended fixes").AppendLine();
            foreach (var x in r.Recommendations) sb.AppendLine($"- [{x.Severity}] {x.Title}: {string.Join("; ", x.Actions)}");
            sb.AppendLine();
        }
        if (r.RelatedEvents.Count > 0)
        {
            sb.AppendLine("### Windows events around this run").AppendLine();
            foreach (var e in r.RelatedEvents.Take(60)) sb.AppendLine($"- `{e.Time:yyyy-MM-dd HH:mm:ss}` **{e.Provider}** ID {e.Id} ({e.LevelName}): {Clean(e.Message, 500)}");
            sb.AppendLine();
        }
        var log = r.Log.Take(200).ToList();
        if (log.Count > 0)
        {
            sb.AppendLine("### Run log").AppendLine();
            foreach (var l in log) sb.AppendLine($"- `{l.Time:HH:mm:ss}` {l.Message}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string CrashText(CrashInfo c, string heading)
    {
        var sb = new StringBuilder();
        sb.AppendLine(heading).AppendLine();
        sb.AppendLine($"- Type: {c.Kind}; PC rebooted: {(c.SystemRebooted ? "yes" : "no")}; last sign of life {c.LastHeartbeat:yyyy-MM-dd HH:mm:ss}");
        if (c.StepName != null) sb.AppendLine($"- Running: \"{c.StepName}\" ({string.Join(" + ", c.ActiveComponents.Select(x => x.Display()))}), {c.RunElapsedSeconds / 60:0.0} min into the run; errors before crash: {c.ErrorsBeforeCrash}");
        if (c.SuspectedComponent != null) sb.AppendLine($"- BenchForge suspects: {c.SuspectedComponent.Value.Display()}");
        foreach (var e in c.Explanation) sb.AppendLine($"- {e}");
        foreach (var d in c.Dumps)
            sb.AppendLine($"- Crash dump {Path.GetFileName(d.Path)} ({d.Created:yyyy-MM-dd HH:mm}): " +
                          (d.Valid ? $"stop code {BugCheckKnowledgeBase.Describe(d.BugCheckCode)}, params {string.Join(", ", d.Parameters.Select(p => $"0x{p:X}"))}{(d.DeepAnalysis != null ? "; debugger: " + d.DeepAnalysis : "")}" : d.Error));
        if (c.Signals.Count > 0)
        {
            sb.AppendLine("- Signals:");
            foreach (var s in c.Signals.Take(30)) sb.AppendLine($"  - `{s.Time:yyyy-MM-dd HH:mm:ss}` {s.Kind}: {s.Title}{(s.Detail.Length > 0 ? " — " + s.Detail : "")}{(s.Module != null ? $" (module {s.Module})" : "")}");
        }
        if (c.Tail.Count > 0)
        {
            var cols = SensorColumns.Where(k => c.Tail.Any(t => t.Metrics.ContainsKey(k))).ToList();
            sb.AppendLine().AppendLine("Last seconds before the crash (1 per second, CSV; `s` = seconds before the last heartbeat):").AppendLine().AppendLine("```csv");
            sb.AppendLine("s," + string.Join(",", cols));
            var last = c.Tail[^1].Time;
            foreach (var t in c.Tail.TakeLast(180))
                sb.AppendLine($"{(t.Time - last).TotalSeconds:0}," + string.Join(",", cols.Select(k => t.Metrics.TryGetValue(k, out var v) ? v.ToString("0.#", CultureInfo.InvariantCulture) : "")));
            sb.AppendLine("```");
        }
        return sb.AppendLine().ToString();
    }

    /// <summary>Key-metric history as CSV, evenly downsampled to at most <paramref name="maxRows"/> rows (keeps peaks of each bucket for temps/power).</summary>
    public static string? SensorTable(SensorTimeline tl, int maxRows)
    {
        var cols = SensorColumns.Select(k => (Key: k, Index: tl.IndexOf(KeyMetrics.ChannelId(k)))).Where(c => c.Index >= 0).ToList();
        var test = tl.Channels.Select((ch, idx) => (ch, idx)).Where(x => x.ch.HwType == HwType.Test).Take(3).ToList();
        if (cols.Count == 0 || tl.Samples.Count == 0) return null;
        var sb = new StringBuilder();
        sb.AppendLine("t," + string.Join(",", cols.Select(c => c.Key)) + string.Concat(test.Select(t => "," + t.ch.Name.Replace(",", " ") + (t.ch.Unit.Length > 0 ? $" ({t.ch.Unit})" : ""))));
        int bucket = Math.Max(1, (int)Math.Ceiling(tl.Samples.Count / (double)maxRows));
        for (int i = 0; i < tl.Samples.Count; i += bucket)
        {
            var group = tl.Samples.Skip(i).Take(bucket).ToList();
            string Val(int idx, bool peak)
            {
                var vals = group.Where(s => idx < s.V.Length && s.V[idx] is float).Select(s => (double)s.V[idx]!.Value).ToList();
                if (vals.Count == 0) return "";
                return (peak ? vals.Max() : vals.Average()).ToString("0.#", CultureInfo.InvariantCulture);
            }
            sb.AppendLine(group[0].T.ToString("0", CultureInfo.InvariantCulture) + "," +
                          string.Join(",", cols.Select(c => Val(c.Index, c.Key.EndsWith("temp") || c.Key.EndsWith("hotspot") || c.Key.EndsWith("power")))) +
                          string.Concat(test.Select(t => "," + Val(t.idx, false))));
        }
        return sb.ToString();
    }

    private static string Cell(string s) => (s ?? "").Replace("|", "/").Replace("\n", " ");

    /// <summary>Full-resolution CSV of every recorded sensor in a run (for the zip bundle / spreadsheets).</summary>
    public static string FullSensorCsv(SensorTimeline tl)
    {
        var sb = new StringBuilder();
        sb.AppendLine("t," + string.Join(",", tl.Channels.Select(c => "\"" + $"{c.Hardware} / {c.Name} ({c.Unit})".Replace("\"", "'") + "\"")));
        foreach (var smp in tl.Samples)
            sb.AppendLine(smp.T.ToString("0.#", CultureInfo.InvariantCulture) + "," +
                          string.Join(",", Enumerable.Range(0, tl.Channels.Count).Select(i => i < smp.V.Length && smp.V[i] is float f ? f.ToString("0.###", CultureInfo.InvariantCulture) : "")));
        return sb.ToString();
    }

    /// <summary>Events as CSV (zip bundle).</summary>
    public static string EventsCsv(IEnumerable<EventEntry> events)
    {
        var sb = new StringBuilder("time,log,provider,id,level,message\n");
        foreach (var e in events)
            sb.AppendLine($"{e.Time:yyyy-MM-dd HH:mm:ss},{e.Log},\"{e.Provider}\",{e.Id},{e.LevelName},\"{(e.Message ?? "").Replace("\"", "'").Replace("\r", " ").Replace("\n", " ")}\"");
        return sb.ToString();
    }

    private static string Clean(string s, int max)
    {
        s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Replace("  ", " ").Trim();
        return s.Length > max ? s[..max] + "…" : s;
    }
}
