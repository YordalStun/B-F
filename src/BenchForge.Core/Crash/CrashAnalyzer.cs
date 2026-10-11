using BenchForge.Core.Events;
using BenchForge.Core.Model;
using BenchForge.Core.Persistence;

namespace BenchForge.Core.Crash;

/// <summary>A crash Windows recorded (whether or not BenchForge was running at the time).</summary>
public sealed class SystemCrash
{
    public DateTime Time { get; set; }
    public CrashInfo Info { get; set; } = new();
    /// <summary>Set when this crash happened during a BenchForge run.</summary>
    public string? RunId { get; set; }
    public string Title => Info.Summary;
}

public static class CrashAnalyzer
{
    /// <summary>
    /// Turns an unfinished session journal into a "Crashed" run result: what was running, the last sensor values,
    /// Windows events around the crash and any new crash dumps.
    /// </summary>
    public static RunResult FromJournal(JournalRecovery rec, IEventSource? events, Func<DateTime, List<MinidumpInfo>>? dumps = null)
    {
        var h = rec.Header;
        var hb = rec.LastHeartbeat;
        var currentBoot = DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);
        bool rebooted = Math.Abs((currentBoot - h.BootTime).TotalMinutes) > 2;

        var result = new RunResult
        {
            Id = h.RunId, Name = h.Name, StartTime = h.StartTime, EndTime = rec.LastAlive, Status = RunStatus.Crashed,
            MachineName = h.MachineName, Plan = h.Plan, Hardware = h.Hardware, Steps = rec.CompletedSteps,
            AppVersion = typeof(CrashAnalyzer).Assembly.GetName().Version?.ToString(3) ?? "",
        };
        result.Log = rec.LogLines.Select(l =>
        {
            var sp = l.IndexOf(' ');
            return new RunLogEntry { Time = sp > 0 && DateTime.TryParse(l[..sp], out var t) ? t : rec.LastAlive, Message = sp > 0 ? l[(sp + 1)..] : l };
        }).ToList();

        var info = new CrashInfo
        {
            LastHeartbeat = rec.LastAlive,
            SystemRebooted = rebooted,
            StepName = hb?.StepName,
            StepKind = hb?.StepKind,
            ActiveComponents = hb?.Components ?? new(),
            RunElapsedSeconds = hb?.RunElapsedSeconds ?? (rec.LastAlive - h.StartTime).TotalSeconds,
            StepElapsedSeconds = hb?.StepElapsedSeconds ?? 0,
            ErrorsBeforeCrash = hb?.ErrorCount ?? 0,
            Tail = hb?.Tail ?? new(),
        };
        if (hb?.Tail.Count > 0)
        {
            info.LastMetrics = hb.Tail[^1].Metrics;
            var lastMinute = hb.Tail.Where(s => s.Time >= hb.Tail[^1].Time.AddSeconds(-60)).ToList();
            foreach (var key in lastMinute.SelectMany(s => s.Metrics.Keys).Distinct())
                info.PeakMetrics[key] = lastMinute.Where(s => s.Metrics.ContainsKey(key)).Max(s => s.Metrics[key]);
        }
        if (hb != null) result.Errors = hb.RecentErrors;
        result.TotalErrorCount = hb?.ErrorCount ?? 0;

        // Timeline from the heartbeat tail so the results screen has graphs of the final minutes.
        if (info.Tail.Count > 0)
        {
            var keys = info.Tail.SelectMany(s => s.Metrics.Keys).Distinct().ToList();
            result.Timeline.Channels = keys.Select(k => new SensorChannel { Id = KeyMetrics.ChannelId(k), Hardware = "Key metrics", Name = KeyMetrics.Label(k), Unit = KeyMetrics.Unit(k), HwType = KeyMetrics.HwTypeOf(k), Kind = KeyMetrics.KindOf(k) }).ToList();
            result.Timeline.Samples = info.Tail.Select(s => new TimelineSample
            {
                T = Math.Round((s.Time - h.StartTime).TotalSeconds, 1),
                V = keys.Select(k => s.Metrics.TryGetValue(k, out var v) ? (float?)v : null).ToArray(),
            }).ToList();
            foreach (var k in keys)
            {
                var vals = info.Tail.Where(s => s.Metrics.ContainsKey(k)).Select(s => s.Metrics[k]).ToList();
                result.KeyStats[k] = new ChannelStats { Id = k, Name = KeyMetrics.Label(k), Unit = KeyMetrics.Unit(k), Min = vals.Min(), Max = vals.Max(), Avg = Math.Round(vals.Average(), 2), Last = vals[^1], Count = vals.Count };
            }
        }

        // Windows events from a little before the crash until now.
        if (events != null)
        {
            try
            {
                var spec = EventPresets.CrashRelated(rec.LastAlive.AddMinutes(-15), DateTime.Now.AddMinutes(1));
                result.RelatedEvents = events.Query(spec, 300);
            }
            catch (Exception ex) { DataPaths.AppLogWrite("Event query failed: " + ex.Message); }
        }
        info.Signals = result.RelatedEvents.Select(EventInterpreter.Interpret).Where(s => s != null).Select(s => s!).OrderBy(s => s.Time).ToList();
        info.Dumps = (dumps ?? (since => MinidumpReader.ListDumps(null, since)))(rec.LastAlive.AddMinutes(-5));

        if (!rebooted && !info.Signals.Any(s => s.Kind is SignalKind.BugCheck or SignalKind.UnexpectedShutdown))
            info.Kind = CrashKind.AppTerminated;
        else if (info.Signals.Any(s => s.Kind == SignalKind.BugCheck) || info.Dumps.Any(d => d.Valid))
            info.Kind = CrashKind.BlueScreen;
        else if (rebooted)
            info.Kind = CrashKind.PowerLossOrHang;

        Explain(info, h.Plan.Settings);
        result.Crash = info;
        PlanRunnerScores(result);
        Analysis.RunAnalyzer.Analyze(result);
        return result;
    }

    private static void PlanRunnerScores(RunResult r) => Engine.PlanRunner.ComputeScores(r);

    /// <summary>
    /// Groups crash events Windows logged (blue screens, Kernel-Power 41, unexpected shutdowns) into individual crashes,
    /// each with the WHEA/GPU/disk warnings that came just before it.
    /// </summary>
    public static List<SystemCrash> FindSystemCrashes(List<EventEntry> events, List<MinidumpInfo>? dumps = null)
    {
        var signals = events.Select(e => EventInterpreter.Interpret(e)).Where(s => s != null).Select(s => s!).OrderBy(s => s.Time).ToList();
        var anchors = signals.Where(s => s.Kind is SignalKind.BugCheck or SignalKind.UnexpectedShutdown).ToList();
        var crashes = new List<SystemCrash>();
        foreach (var a in anchors)
        {
            // Kernel-Power 41, 6008 and BugCheck 1001 are all logged during the next boot: merge those within 10 min.
            var existing = crashes.FirstOrDefault(c => Math.Abs((c.Time - a.Time).TotalMinutes) < 10);
            if (existing != null)
            {
                if (!existing.Info.Signals.Contains(a)) existing.Info.Signals.Add(a);
                continue;
            }
            crashes.Add(new SystemCrash { Time = a.Time, Info = new CrashInfo { DetectedAt = a.Time, LastHeartbeat = a.Time, SystemRebooted = true, Signals = { a } } });
        }
        foreach (var c in crashes)
        {
            // Warnings in the 30 minutes before the reboot.
            foreach (var s in signals.Where(s => s.Kind is not (SignalKind.BugCheck or SignalKind.UnexpectedShutdown) && s.Time <= c.Time && s.Time >= c.Time.AddMinutes(-30)))
                c.Info.Signals.Add(s);
            c.Info.Signals = c.Info.Signals.OrderBy(s => s.Time).ToList();
            if (dumps != null)
            {
                var d = dumps.Where(d => Math.Abs((d.Created - c.Time).TotalMinutes) < 20).OrderBy(d => Math.Abs((d.Created - c.Time).TotalMinutes)).FirstOrDefault();
                if (d != null) c.Info.Dumps.Add(d);
            }
            // The actual crash happened before the reboot event; Kernel-Power 41 is logged at boot.
            c.Info.Kind = c.Info.Signals.Any(s => s.Kind == SignalKind.BugCheck) || c.Info.Dumps.Any(d => d.Valid) ? CrashKind.BlueScreen : CrashKind.PowerLossOrHang;
            Explain(c.Info, null);
        }
        return crashes.OrderByDescending(c => c.Time).ToList();
    }

    /// <summary>Fills Summary, Explanation and SuspectedComponent from everything known about a crash.</summary>
    public static void Explain(CrashInfo c, TestSettings? settings)
    {
        var ex = new List<string>();
        var votes = new Dictionary<Component, double>();
        void Vote(Component? comp, double w) { if (comp is Component x) votes[x] = votes.GetValueOrDefault(x) + w; }

        uint? code = c.Signals.FirstOrDefault(s => s.BugCheckCode is > 0)?.BugCheckCode ?? c.Dumps.FirstOrDefault(d => d.Valid && d.BugCheckCode != 0)?.BugCheckCode;
        var kb = code is uint cc ? BugCheckKnowledgeBase.Get(cc) : null;
        var dump = c.Dumps.FirstOrDefault(d => d.Valid);

        switch (c.Kind)
        {
            case CrashKind.BlueScreen:
                c.Summary = code is uint bc ? $"Blue screen {BugCheckKnowledgeBase.Describe(bc)}" : "Blue screen";
                if (kb != null)
                {
                    ex.Add(kb.Meaning);
                    Vote(kb.LikelyComponent, 3);
                }
                if (dump != null)
                {
                    var p = BugCheckKnowledgeBase.DescribeParameters(dump.BugCheckCode, dump.Parameters);
                    if (p != null) ex.Add(p + ".");
                    if (dump.FaultingModule != null) ex.Add($"Crash dump analysis points at: {dump.FaultingModule}.");
                }
                break;
            case CrashKind.PowerLossOrHang:
                c.Summary = "Sudden reboot / power loss (no blue screen)";
                ex.Add("Windows did not get to show a blue screen. That happens when the PC freezes solid and is reset, when the power supply shuts off, or when a thermal/over-current protection trips.");
                break;
            case CrashKind.AppTerminated:
                c.Summary = "BenchForge closed unexpectedly (Windows kept running)";
                ex.Add("The PC did not reboot. BenchForge was closed, killed, or crashed itself — this is not a hardware crash on its own.");
                break;
            case CrashKind.GpuProcessCrash:
                c.Summary = "3D test crashed (graphics driver)";
                Vote(Component.Gpu, 3);
                break;
            default:
                c.Summary = "Unexpected stop";
                break;
        }

        if (c.StepName != null)
        {
            var comps = string.Join(" + ", c.ActiveComponents.Select(x => x.Display()));
            ex.Add($"It happened {Model.TimeFormat.Short(TimeSpan.FromSeconds(c.RunElapsedSeconds))} into the run, during \"{c.StepName}\" ({(c.StepKind == StepKind.Stress ? "stressing" : "benchmarking")} {comps}).");
            if (c.ActiveComponents.Count == 1) Vote(c.ActiveComponents[0], 2);
            else foreach (var comp in c.ActiveComponents) Vote(comp, 0.5);
        }

        // Temperatures and power right before the crash.
        double Peak(string k) => c.PeakMetrics.TryGetValue(k, out var v) ? v : c.LastMetrics.GetValueOrDefault(k, double.NaN);
        double cpuT = Peak(KeyMetrics.CpuTemp), gpuT = Peak(KeyMetrics.GpuTemp), hot = Peak(KeyMetrics.GpuHotspot);
        double cpuP = Peak(KeyMetrics.CpuPower), gpuP = Peak(KeyMetrics.GpuPower);
        if (!double.IsNaN(cpuT) && cpuT >= 90) { ex.Add($"CPU temperature was very high ({cpuT:0} °C) just before the crash."); Vote(Component.Cpu, 1.5); }
        if (!double.IsNaN(gpuT) && gpuT >= 85 || !double.IsNaN(hot) && hot >= 100) { ex.Add($"GPU was very hot ({gpuT:0} °C core{(double.IsNaN(hot) ? "" : $", {hot:0} °C hot spot")})."); Vote(Component.Gpu, 1.5); }
        double totalP = (double.IsNaN(cpuP) ? 0 : cpuP) + (double.IsNaN(gpuP) ? 0 : gpuP);
        if (c.Kind == CrashKind.PowerLossOrHang && totalP > 350)
            ex.Add($"CPU + GPU were drawing about {totalP:0} W at the time. A sudden power-off under heavy load is a classic sign of a power supply that can't keep up (or is failing).");
        var v12 = Peak(KeyMetrics.Rail12V);
        if (!double.IsNaN(v12) && v12 > 5 && v12 < 11.4) ex.Add($"The +12V rail read {v12:0.00} V (below spec 11.4 V) — check the power supply.");

        foreach (var s in c.Signals.Where(s => s.Kind is SignalKind.WheaFatal or SignalKind.WheaCorrected).GroupBy(s => s.HardwareSource ?? "").Take(3))
        {
            ex.Add($"{s.Count()}× {(s.First().Kind == SignalKind.WheaFatal ? "fatal" : "corrected")} hardware error from {(string.IsNullOrWhiteSpace(s.Key) ? "the CPU/bus" : s.Key)} ({s.First().Detail}).");
            Vote(s.First().Component ?? Component.Cpu, s.First().Kind == SignalKind.WheaFatal ? 3 : 1);
        }
        var tdr = c.Signals.Where(s => s.Kind == SignalKind.GpuDriverReset).ToList();
        if (tdr.Count > 0) { ex.Add($"The graphics driver ({tdr[0].Module ?? "display driver"}) reset {tdr.Count}× shortly before."); Vote(Component.Gpu, 2); }
        var disk = c.Signals.Where(s => s.Kind == SignalKind.DiskError).ToList();
        if (disk.Count > 0) { ex.Add($"{disk.Count} drive/file-system errors were logged around the crash ({disk[0].Title})."); Vote(Component.Storage, 1.5); }
        if (c.Signals.Any(s => s.Kind == SignalKind.ThermalThrottle)) ex.Add("Windows logged CPU throttling by firmware (heat or power limit).");
        if (c.ErrorsBeforeCrash > 0) ex.Add($"BenchForge had already found {c.ErrorsBeforeCrash} calculation/memory errors before the crash — the hardware was producing wrong results.");

        // When only BenchForge stopped (no reboot, no hardware signals) the load at the time says nothing about the hardware.
        if (c.Kind == CrashKind.AppTerminated && !c.Signals.Any(s => s.Kind is SignalKind.WheaFatal or SignalKind.GpuDriverReset) && c.ErrorsBeforeCrash == 0)
            votes.Clear();
        if (votes.Count > 0)
        {
            var top = votes.OrderByDescending(v => v.Value).First();
            c.SuspectedComponent = top.Key;
            ex.Add($"Most likely area: {top.Key.Display()}.");
        }
        if (kb != null && kb.Causes.Length > 0) ex.Add("Common causes: " + string.Join("; ", kb.Causes) + ".");
        c.Explanation = ex;
    }
}
