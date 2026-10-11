using BenchForge.Core.Crash;
using BenchForge.Core.Model;

namespace BenchForge.Core.Analysis;

/// <summary>One failure (crash or errors) normalised so different runs/crashes can be compared.</summary>
public sealed class FailureCase
{
    public string Label { get; set; } = "";
    public DateTime Time { get; set; }
    public string Machine { get; set; } = "";
    public bool IsCrash { get; set; }
    public CrashKind CrashKind { get; set; }
    public uint? BugCheck { get; set; }
    public string? StepName { get; set; }
    public StepKind? StepKind { get; set; }
    public List<Component> ActiveComponents { get; set; } = new();
    public double? MinutesIntoRun { get; set; }
    public Dictionary<string, double> Peak { get; set; } = new();
    public List<CrashSignal> Signals { get; set; } = new();
    public List<string> Modules { get; set; } = new();
    public Dictionary<Component, int> ErrorsByComponent { get; set; } = new();
    /// <summary>Logical CPU numbers that produced calculation errors.</summary>
    public List<int> ErrorThreads { get; set; } = new();
    public List<string> RamBits { get; set; } = new();
    public string? HardwareFingerprint { get; set; }
    public Component? Suspected { get; set; }
    /// <summary>Run used laptop rules (CPU at ~95–100 °C is normal).</summary>
    public bool Laptop { get; set; }
    /// <summary>Only BenchForge stopped (closed/killed) — no hardware evidence at all.</summary>
    public bool AppOnly { get; set; }

    public static FailureCase FromRun(RunResult r)
    {
        var fc = new FailureCase
        {
            Label = $"{r.DisplayName} ({r.StartTime:dd MMM HH:mm})",
            Time = r.Crash?.LastHeartbeat ?? r.StartTime,
            Machine = r.ImportedFrom ?? r.MachineName,
            IsCrash = r.Crash != null,
            CrashKind = r.Crash?.Kind ?? CrashKind.Unknown,
            Suspected = r.Crash?.SuspectedComponent,
            Laptop = r.Plan.Settings.LaptopRules,
            HardwareFingerprint = r.Hardware == null ? null : $"{r.Hardware.Cpu.Name}|{string.Join(",", r.Hardware.Gpus.Select(g => g.Name))}|{r.Hardware.TotalMemoryGB:0}",
        };
        if (r.Crash is CrashInfo c)
        {
            fc.BugCheck = c.Signals.FirstOrDefault(s => s.BugCheckCode is > 0)?.BugCheckCode ?? c.Dumps.FirstOrDefault(d => d.Valid && d.BugCheckCode > 0)?.BugCheckCode;
            fc.StepName = c.StepName;
            fc.StepKind = c.StepKind;
            fc.ActiveComponents = c.ActiveComponents;
            fc.MinutesIntoRun = c.RunElapsedSeconds / 60;
            fc.Peak = c.PeakMetrics.Count > 0 ? c.PeakMetrics : c.LastMetrics;
            fc.Signals = c.Signals;
            fc.Modules.AddRange(c.Dumps.Select(d => d.FaultingModule).Where(m => m != null)!);
        }
        else
        {
            // Errors without crash: use the step with the first error.
            var firstErr = r.Errors.OrderBy(e => e.Time).FirstOrDefault();
            var step = firstErr == null ? null : r.Steps.LastOrDefault(s => s.Start <= firstErr.Time);
            fc.StepName = step?.Name;
            fc.StepKind = step?.Kind;
            fc.ActiveComponents = step?.Components ?? new();
            fc.MinutesIntoRun = firstErr == null ? null : (firstErr.Time - r.StartTime).TotalMinutes;
            fc.Peak = r.KeyStats.ToDictionary(k => k.Key, k => k.Value.Max);
        }
        foreach (var s in fc.Signals.Where(s => s.Module != null)) fc.Modules.Add(s.Module!);
        foreach (var m in r.AllModules.Where(m => m.ErrorCount > 0 || (m.Failed && m.FailureReason != "Cancelled")))
            fc.ErrorsByComponent[m.Component] = fc.ErrorsByComponent.GetValueOrDefault(m.Component) + Math.Max(1, m.ErrorCount);
        if (r.Crash != null && fc.ErrorsByComponent.Count == 0 && r.Errors.Count > 0)
            foreach (var g in r.Errors.GroupBy(e => e.Component)) fc.ErrorsByComponent[g.Key] = g.Count();
        fc.AppOnly = fc.CrashKind == CrashKind.AppTerminated && fc.ErrorsByComponent.Count == 0 &&
                     !fc.Signals.Any(s => s.Kind is SignalKind.WheaFatal or SignalKind.WheaCorrected or SignalKind.GpuDriverReset or SignalKind.BugCheck);
        if (fc.AppOnly) fc.Suspected = null;
        fc.ErrorThreads = r.Errors.Where(e => e.Component == Component.Cpu && e.Thread != null).Select(e => e.Thread!.Value).Distinct().OrderBy(x => x).ToList();
        foreach (var m in r.AllModules.Where(m => m.Info.ContainsKey("Failing bit positions")))
            fc.RamBits.AddRange(m.Info["Failing bit positions"].Split(", ").Select(b => b.Split(' ')[0] + " " + b.Split(' ')[1]));
        return fc;
    }

    public static FailureCase FromSystemCrash(SystemCrash c) => new()
    {
        Label = $"{c.Info.Summary} ({c.Time:dd MMM HH:mm})",
        Time = c.Time,
        IsCrash = true,
        CrashKind = c.Info.Kind,
        BugCheck = c.Info.Signals.FirstOrDefault(s => s.BugCheckCode is > 0)?.BugCheckCode ?? c.Info.Dumps.FirstOrDefault(d => d.Valid)?.BugCheckCode,
        Signals = c.Info.Signals,
        Modules = c.Info.Signals.Where(s => s.Module != null).Select(s => s.Module!).Concat(c.Info.Dumps.Where(d => d.FaultingModule != null).Select(d => d.FaultingModule!)).ToList(),
        Suspected = c.Info.SuspectedComponent,
        StepName = c.Info.StepName,
        ActiveComponents = c.Info.ActiveComponents,
        Peak = c.Info.PeakMetrics,
    };
}

public sealed class PatternReport
{
    public int CaseCount { get; set; }
    public List<Finding> Findings { get; set; } = new();
    public Component? Suspected { get; set; }
    public string Verdict { get; set; } = "";
    public List<string> NextSteps { get; set; } = new();
}

/// <summary>
/// Compares two or more failures (crashed runs, runs with errors, or Windows crashes) and reports what they have in common.
/// Entirely offline and rule-based.
/// </summary>
public static class PatternAnalyzer
{
    public static PatternReport Analyze(IReadOnlyList<FailureCase> allCases, IReadOnlyList<RunResult>? passingRuns = null)
    {
        var rep = new PatternReport { CaseCount = allCases.Count };
        // Runs where only BenchForge was closed carry no hardware evidence: report them but don't let them vote.
        var appOnly = allCases.Where(c => c.AppOnly).ToList();
        var cases = allCases.Where(c => !c.AppOnly).ToList();
        if (appOnly.Count > 0)
            rep.Findings.Add(new Finding { Severity = Severity.Info, Title = $"{appOnly.Count} of the selected runs only had BenchForge close unexpectedly", Detail = "Windows kept running and no hardware errors were logged, so these are not counted as hardware crashes.", Confidence = 1 });
        if (cases.Count == 0 && appOnly.Count > 0)
        {
            rep.Verdict = "No hardware crash to analyse — BenchForge itself was closed or killed. If you didn't close it, check antivirus and run the test again.";
            return rep;
        }
        if (cases.Count == 0)
        {
            rep.Verdict = "None of the selected items are crashes or runs with errors, so there is no failure pattern to look for.";
            return rep;
        }
        int n = cases.Count;
        var votes = new Dictionary<Component, double>();
        void Vote(Component c, double w) => votes[c] = votes.GetValueOrDefault(c) + w;
        void F(Severity s, string title, string detail, int hits, Component? comp = null) =>
            rep.Findings.Add(new Finding { Severity = s, Title = title, Detail = detail, Confidence = (double)hits / n, Component = comp });
        string Of(int k) => k == n ? (n == 2 ? "Both" : $"All {n}") : $"{k} of {n}";

        // 1. Same stop code.
        foreach (var g in cases.Where(c => c.BugCheck != null).GroupBy(c => c.BugCheck!.Value).Where(g => g.Count() >= 2 || n == 1))
        {
            var kb = BugCheckKnowledgeBase.Get(g.Key);
            F(Severity.Critical, $"{Of(g.Count())} crashes have the same stop code {BugCheckKnowledgeBase.Describe(g.Key)}",
              kb != null ? kb.Meaning + " Typical causes: " + string.Join("; ", kb.Causes.Take(3)) + "." : "Same stop code repeating points at one consistent cause.", g.Count(), kb?.LikelyComponent);
            if (kb?.LikelyComponent is Component kc) Vote(kc, 3.0 * g.Count() / n);
        }
        var codes = cases.Where(c => c.BugCheck != null).Select(c => c.BugCheck!.Value).Distinct().ToList();
        if (codes.Count >= 3 && codes.Count >= cases.Count(c => c.BugCheck != null) * 0.75)
        {
            F(Severity.Warning, "Many different stop codes", $"The blue screens have {codes.Count} different stop codes ({string.Join(", ", codes.Select(c => $"0x{c:X}"))}). Random, changing stop codes are the classic signature of unstable RAM or CPU (or a failing PSU), not a single bad driver.", codes.Count);
            Vote(Component.Memory, 2); Vote(Component.Cpu, 1);
        }

        // 2. No blue screen — sudden power-offs.
        int power = cases.Count(c => c.CrashKind == CrashKind.PowerLossOrHang);
        if (power >= 2 || power == n)
        {
            var highLoad = cases.Where(c => c.CrashKind == CrashKind.PowerLossOrHang && c.Peak.GetValueOrDefault(KeyMetrics.CpuPower) + c.Peak.GetValueOrDefault(KeyMetrics.GpuPower) > 300).ToList();
            F(Severity.Critical, $"{Of(power)} were sudden shut-offs without a blue screen",
              highLoad.Count > 0 ? $"{highLoad.Count} of them happened while CPU+GPU drew over 300 W. Sudden shut-offs under heavy load strongly suggest the power supply (overload/protection trip/failing) or overheating VRMs."
                                 : "Repeated hard shut-offs point to power delivery (PSU, cables, wall power) or a hard freeze from unstable settings.", power);
        }

        // 3. Same component under load.
        foreach (var comp in Enum.GetValues<Component>())
        {
            int k = cases.Count(c => c.ActiveComponents.Contains(comp));
            int alone = cases.Count(c => c.ActiveComponents.Count == 1 && c.ActiveComponents[0] == comp);
            if (k >= 2 && k == cases.Count(c => c.ActiveComponents.Count > 0))
            {
                F(alone >= 2 ? Severity.Critical : Severity.Warning, $"{Of(k)} failures happened while the {comp.Display()} was under load",
                  alone >= 2 ? $"{alone} of them happened when ONLY the {comp.Display()} was being tested — that isolates it as the likely cause." : $"The {comp.Display()} was always being stressed when it failed (together with other parts).", k, comp);
                Vote(comp, alone >= 2 ? 3 : 1);
            }
        }
        var sameStep = cases.Where(c => c.StepName != null).GroupBy(c => StripLoop(c.StepName!)).FirstOrDefault(g => g.Count() >= 2);
        if (sameStep != null)
            F(Severity.Warning, $"{Of(sameStep.Count())} failed during the same test: \"{sameStep.Key}\"", "The same workload triggers it each time — the failure is reproducible, which makes it easier to confirm a fix.", sameStep.Count());

        // 4. Timing.
        var mins = cases.Where(c => c.MinutesIntoRun != null).Select(c => c.MinutesIntoRun!.Value).ToList();
        if (mins.Count >= 2)
        {
            double avg = mins.Average();
            if (avg > 3 && mins.All(m => Math.Abs(m - avg) <= Math.Max(4, avg * 0.25)))
                F(Severity.Warning, $"Failures happen after a similar time (~{avg:0} min into the test)", "A consistent delay before failing usually means heat soak: something (CPU, GPU, VRMs, RAM, PSU) gets hotter over minutes until it becomes unstable.", mins.Count);
            else if (mins.All(m => m < 2))
                F(Severity.Warning, "Failures happen almost immediately under load", "Failing within seconds of load starting points at a hard instability (overclock/undervolt, power delivery) rather than slowly building heat.", mins.Count);
        }

        // 5. Temperatures at the time.
        foreach (var (key, limit, comp) in new[] { (KeyMetrics.CpuTemp, 90.0, Component.Cpu), (KeyMetrics.GpuTemp, 83.0, Component.Gpu), (KeyMetrics.GpuHotspot, 100.0, Component.Gpu), (KeyMetrics.GpuMemTemp, 98.0, Component.Gpu), (KeyMetrics.DiskTemp, 70.0, Component.Storage) })
        {
            var hot = cases.Where(c => c.Peak.TryGetValue(key, out var v) && v >= (c.Laptop && key == KeyMetrics.CpuTemp ? 100 : limit)).ToList();
            int withData = cases.Count(c => c.Peak.ContainsKey(key));
            if (hot.Count >= 2 || (hot.Count >= 1 && withData == 1 && n == 1))
            {
                F(Severity.Critical, $"{Of(hot.Count)} failures happened with {KeyMetrics.Label(key)} ≥ {limit:0} °C",
                  $"Peaks: {string.Join(", ", hot.Select(c => $"{c.Peak[key]:0} °C"))}. Heat is a likely trigger — improve cooling and re-test.", hot.Count, comp);
                Vote(comp, 2);
            }
            else if (withData >= 2 && hot.Count == 0 && key == KeyMetrics.CpuTemp)
                F(Severity.Info, "Temperatures were normal at every failure", $"{KeyMetrics.Label(key)} peaked at {string.Join(", ", cases.Where(c => c.Peak.ContainsKey(key)).Select(c => $"{c.Peak[key]:0} °C"))} — overheating is unlikely to be the cause.", withData);
        }

        // 6. WHEA sources.
        var whea = cases.Where(c => c.Signals.Any(s => s.Kind is SignalKind.WheaFatal or SignalKind.WheaCorrected)).ToList();
        if (whea.Count >= 2 || (whea.Count == 1 && n == 1))
        {
            var sources = whea.SelectMany(c => c.Signals.Where(s => s.Kind is SignalKind.WheaFatal or SignalKind.WheaCorrected).Select(s => s.HardwareSource).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct()).GroupBy(s => s).OrderByDescending(g => g.Count()).ToList();
            var top = sources.FirstOrDefault();
            F(Severity.Critical, $"{Of(whea.Count)} have hardware (WHEA) errors",
              top != null ? $"Most common source: {top.Key} ({top.Count()}×). " + (top.Key!.Contains("APIC") ? "The same APIC ID every time means one specific CPU core is unstable — lower its overclock/Curve Optimizer offset." : top.Key.Contains("PCI", StringComparison.OrdinalIgnoreCase) ? "PCI Express errors: reseat the GPU/NVMe, check risers, try PCIe Gen 3 in BIOS." : "") : "The CPU/bus reported hardware errors around each failure.", whea.Count);
            foreach (var s in whea.SelectMany(c => c.Signals).Where(s => s.Component != null)) Vote(s.Component!.Value, 1.5 / n);
            if (whea.All(c => c.Signals.Any(s => s.HardwareSource?.Contains("Processor", StringComparison.OrdinalIgnoreCase) == true))) Vote(Component.Cpu, 2);
        }

        // 7. Same drivers / modules.
        foreach (var g in cases.SelectMany(c => c.Modules.Distinct(StringComparer.OrdinalIgnoreCase).Select(m => (Case: c, Module: m))).GroupBy(x => x.Module, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() >= 2))
        {
            bool gpuDriver = g.Key.StartsWith("nv", StringComparison.OrdinalIgnoreCase) || g.Key.StartsWith("amd", StringComparison.OrdinalIgnoreCase) || g.Key.StartsWith("ati", StringComparison.OrdinalIgnoreCase) || g.Key.StartsWith("igfx", StringComparison.OrdinalIgnoreCase);
            F(Severity.Critical, $"{Of(g.Count())} involve the same driver/module: {g.Key}",
              gpuDriver ? "This is the graphics driver. Clean-reinstall it (DDU), remove GPU overclocks, and check GPU power cables." : "The same module keeps appearing. Update or remove the software/driver it belongs to.", g.Count(), gpuDriver ? Component.Gpu : null);
            if (gpuDriver) Vote(Component.Gpu, 2);
        }
        int tdr = cases.Count(c => c.Signals.Any(s => s.Kind == SignalKind.GpuDriverReset));
        if (tdr >= 2) { F(Severity.Critical, $"{Of(tdr)} had GPU driver resets (TDR)", "The graphics card stopped responding each time.", tdr, Component.Gpu); Vote(Component.Gpu, 2); }
        int disk = cases.Count(c => c.Signals.Any(s => s.Kind == SignalKind.DiskError));
        if (disk >= 2) { F(Severity.Warning, $"{Of(disk)} had drive errors around the failure", "Check the drive health and cables.", disk, Component.Storage); Vote(Component.Storage, 1.5); }

        // 8. Calculation / memory errors.
        foreach (var comp in Enum.GetValues<Component>())
        {
            int k = cases.Count(c => c.ErrorsByComponent.ContainsKey(comp));
            if (k >= 2 || (k == 1 && n == 1))
            {
                F(Severity.Critical, $"{Of(k)} had {comp.Display()} errors", comp switch
                {
                    Component.Cpu => "The CPU produced wrong results in each case.",
                    Component.Memory => "RAM returned corrupted data in each case.",
                    Component.Gpu => "The GPU test failed or found VRAM errors in each case.",
                    _ => "The drive returned bad data in each case.",
                }, k, comp);
                Vote(comp, 3);
            }
        }
        var threadSets = cases.Where(c => c.ErrorThreads.Count > 0).Select(c => c.ErrorThreads).ToList();
        if (threadSets.Count >= 2)
        {
            var common = threadSets.Aggregate((a, b) => a.Intersect(b).ToList());
            if (common.Count > 0 && common.Count <= 4)
                F(Severity.Critical, $"Errors keep coming from the same CPU thread(s): {string.Join(", ", common.Select(t => $"CPU{t}"))}",
                  $"Logical CPU {string.Join("/", common)} ≈ physical core {string.Join("/", common.Select(t => t / 2).Distinct())} (with SMT). One weak core — on Ryzen raise its Curve Optimizer value, on Intel lower its per-core ratio.", threadSets.Count, Component.Cpu);
        }
        var bits = cases.Where(c => c.RamBits.Count > 0).Select(c => c.RamBits).ToList();
        if (bits.Count >= 2)
        {
            var commonBits = bits.Aggregate((a, b) => a.Intersect(b).ToList());
            if (commonBits.Count > 0)
                F(Severity.Critical, $"RAM errors hit the same bit position(s) each time: {string.Join(", ", commonBits)}", "A repeating bit points to one physical chip/stick rather than general instability — test the sticks individually.", bits.Count, Component.Memory);
        }

        // 9. Different PCs?
        var machines = cases.Select(c => c.Machine).Where(m => !string.IsNullOrEmpty(m)).Distinct().ToList();
        if (machines.Count > 1)
            F(Severity.Info, $"Results come from {machines.Count} different PCs", string.Join(", ", machines) + ". Patterns shared between different PCs point at software (drivers, Windows) rather than one PC's hardware.", n);

        // 10. Compare against passing runs: did failures happen hotter than successes?
        if (passingRuns is { Count: > 0 })
        {
            var okCpu = passingRuns.Where(r => r.KeyStats.ContainsKey(KeyMetrics.CpuTemp)).Select(r => r.KeyStats[KeyMetrics.CpuTemp].Max).ToList();
            var badCpu = cases.Where(c => c.Peak.ContainsKey(KeyMetrics.CpuTemp)).Select(c => c.Peak[KeyMetrics.CpuTemp]).ToList();
            if (okCpu.Count > 0 && badCpu.Count > 0 && badCpu.Average() - okCpu.Average() >= 8)
                F(Severity.Warning, "Failed runs were hotter than passing runs", $"CPU peaked at {badCpu.Average():0} °C in failures vs {okCpu.Average():0} °C in runs that passed.", badCpu.Count, Component.Cpu);
        }

        // Suspected component from all clues + per-case suspects.
        foreach (var c in cases.Where(c => c.Suspected != null)) Vote(c.Suspected!.Value, 1.0);
        if (votes.Count > 0)
        {
            var ranked = votes.OrderByDescending(v => v.Value).ToList();
            rep.Suspected = ranked[0].Key;
            double share = ranked[0].Value / votes.Values.Sum();
            string confidence = share > 0.6 ? "high" : share > 0.4 ? "medium" : "low";
            rep.Verdict = $"Most likely cause: {ranked[0].Key.Display()} ({confidence} confidence)" +
                          (ranked.Count > 1 && ranked[1].Value > ranked[0].Value * 0.6 ? $", with {ranked[1].Key.Display()} as a close second." : ".");
            rep.NextSteps = NextSteps(ranked[0].Key, power >= 2);
        }
        else if (power >= 2)
        {
            rep.Verdict = "Most likely cause: power delivery (PSU, cables or wall power).";
            rep.NextSteps = NextSteps(null, true);
        }
        else
        {
            rep.Verdict = rep.Findings.Count > 0 ? "The failures share some traits (below) but there isn't enough evidence to blame one part yet." : "No common pattern found between the selected failures — they may have different causes.";
            rep.NextSteps = new() { "Run the Stability rotation test for 1–2 hours: it tests each part alone so a repeat failure points at one part", "Enable 'Reopen after crash' so every crash gets recorded with sensor data" };
        }
        rep.Findings = rep.Findings.OrderByDescending(f => f.Severity).ThenByDescending(f => f.Confidence).ToList();
        return rep;
    }

    private static string StripLoop(string s)
    {
        int i = s.IndexOf(" (loop", StringComparison.Ordinal);
        s = i > 0 ? s[..i] : s;
        return System.Text.RegularExpressions.Regex.Replace(s, @"^Burn \d+$", "Burn test");
    }

    private static List<string> NextSteps(Component? c, bool power) => c switch
    {
        Component.Cpu => new() { "Reset CPU settings to stock in BIOS (no overclock/undervolt/PBO curve) and update the BIOS", "Run CPU stress (Mixed) for 1 hour — 0 errors required", "Check cooler mounting and temperatures" },
        Component.Memory => new() { "Disable XMP/EXPO and run the RAM error test for 1 hour", "If clean: re-enable XMP at a lower speed or with slightly more voltage", "If errors continue at stock: test each stick alone to find the bad one" },
        Component.Gpu => new() { "Remove GPU overclock/undervolt and clean-install the driver (DDU)", "Run GPU stress for 30 minutes; watch hot-spot and VRAM temperatures", "Use separate PCIe power cables; if it still fails at stock, test the card in another PC" },
        Component.Storage => new() { "Back up data now", "Check SMART health and update SSD firmware", "Replace SATA cable / try another M.2 slot" },
        _ when power => new() { "Check PSU wattage and age; test with a known-good PSU if possible", "Use separate GPU power cables and plug directly into the wall", "Run CPU-only and GPU-only stress tests to see which load trips it" },
        _ => new() { "Run the Stability rotation test to isolate the part" },
    };
}

/// <summary>Side-by-side score table for several runs.</summary>
public static class RunComparison
{
    public sealed record Row(string Group, string Name, string Unit, bool HigherIsBetter, double?[] Values);

    public static List<Row> Build(IReadOnlyList<RunResult> runs)
    {
        var rows = new List<Row>();
        rows.Add(new Row("Overall", "Overall score", "pts", true, runs.Select(r => r.OverallScore).ToArray()));
        foreach (var c in Enum.GetValues<Component>())
            if (runs.Any(r => r.ComponentScores.ContainsKey(c)))
                rows.Add(new Row("Overall", $"{c.Display()} score", "pts", true, runs.Select(r => r.ComponentScores.TryGetValue(c, out var v) ? (double?)v : null).ToArray()));
        var subKeys = runs.SelectMany(r => r.AllModules.Where(m => m.Kind == StepKind.Benchmark).SelectMany(m => m.SubScores.Select(s => (m.Component, s.Group, s.Name, s.Unit, s.HigherIsBetter)))).Distinct().ToList();
        foreach (var k in subKeys)
            rows.Add(new Row($"{k.Component.Display()} · {k.Group}", k.Name, k.Unit, k.HigherIsBetter, runs.Select(r =>
            {
                var vals = r.AllModules.Where(m => m.Component == k.Component && m.Kind == StepKind.Benchmark).SelectMany(m => m.SubScores).Where(s => s.Name == k.Name && s.Group == k.Group).Select(s => s.Value).ToList();
                return vals.Count > 0 ? (double?)vals.Average() : null;
            }).ToArray()));
        foreach (var (id, label, unit) in KeyMetrics.All)
            if (runs.Any(r => r.KeyStats.ContainsKey(id)))
                rows.Add(new Row("Sensors (max)", label, unit, false, runs.Select(r => r.KeyStats.TryGetValue(id, out var s) ? (double?)s.Max : null).ToArray()));
        rows.Add(new Row("Stability", "Errors", "", false, runs.Select(r => (double?)r.TotalErrorCount).ToArray()));
        return rows;
    }
}
