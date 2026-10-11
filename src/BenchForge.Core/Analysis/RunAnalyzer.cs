using BenchForge.Core.Model;

namespace BenchForge.Core.Analysis;

/// <summary>Looks through one finished (or crashed) run and produces findings + recommended fixes.</summary>
public static class RunAnalyzer
{
    public static void Analyze(RunResult r)
    {
        var f = new List<Finding>();
        bool laptop = r.Plan.Settings.LaptopRules;
        if (laptop) LaptopTemperatures(r, f); else Temperatures(r, f);
        Cooling(r, f, laptop);
        if (laptop) LaptopClocks(r, f); else Throttling(r, f);
        PowerAndBattery(r, f);
        Errors(r, f);
        Performance(r, f);
        Power(r, f);
        r.Findings = f.OrderByDescending(x => x.Severity).ToList();
        r.Recommendations = RecommendationEngine.Build(r);
    }

    private static double? Max(RunResult r, string key) => r.KeyStats.TryGetValue(key, out var s) ? s.Max : null;
    private static double? Avg(RunResult r, string key) => r.KeyStats.TryGetValue(key, out var s) ? s.Avg : null;

    /// <summary>Laptops are designed to sit at the thermal limit; judge sustained performance instead of temperature.</summary>
    private static void LaptopTemperatures(RunResult r, List<Finding> f)
    {
        if (Max(r, KeyMetrics.CpuTemp) is double ct)
        {
            double atLimit = ShareAtOrAbove(r, KeyMetrics.CpuTemp, 94);
            if (ct >= 105) f.Add(new Finding { Severity = Severity.Critical, Component = Component.Cpu, Title = $"CPU went above its maximum ({ct:0} °C)", Detail = "Even laptops should not exceed ~100–105 °C. The cooling system may be blocked or failing." });
            else if (ct >= 94) f.Add(new Finding { Severity = Severity.Info, Component = Component.Cpu, Title = $"CPU ran at its thermal limit ({ct:0} °C, {atLimit:P0} of the time) — normal for laptops", Detail = "Laptop CPUs are designed to run at their limit and slow down just enough to stay there. What matters is how much performance they keep — see the cooling result." });
            else f.Add(new Finding { Severity = Severity.Info, Component = Component.Cpu, Title = $"CPU temperature OK (max {ct:0} °C)" });
        }
        if (Max(r, KeyMetrics.GpuTemp) is double gt)
        {
            if (gt >= 95) f.Add(new Finding { Severity = Severity.Warning, Component = Component.Gpu, Title = $"GPU very hot ({gt:0} °C)", Detail = "Above what most laptop GPUs allow. Check vents and fans for dust." });
            else if (gt >= 85) f.Add(new Finding { Severity = Severity.Info, Component = Component.Gpu, Title = $"GPU at its laptop limit ({gt:0} °C) — normal under full load" });
            else f.Add(new Finding { Severity = Severity.Info, Component = Component.Gpu, Title = $"GPU temperature OK (max {gt:0} °C)" });
        }
        if (Max(r, KeyMetrics.GpuHotspot) is double hs && hs >= 110)
            f.Add(new Finding { Severity = Severity.Warning, Component = Component.Gpu, Title = $"GPU hot spot very high ({hs:0} °C)", Detail = "Could mean worn thermal paste or a blocked heatsink." });
        if (Max(r, KeyMetrics.DiskTemp) is double dt && dt >= 75)
            f.Add(new Finding { Severity = Severity.Warning, Component = Component.Storage, Title = $"Drive runs hot ({dt:0} °C)", Detail = "Laptop NVMe drives throttle when hot; heavy writes will slow down." });
        if (Max(r, KeyMetrics.CpuTemp) is double c2 && c2 > 80 && r.KeyStats.TryGetValue(KeyMetrics.CpuFan, out var fan) && fan.Max < 100)
            f.Add(new Finding { Severity = Severity.Critical, Component = Component.Cpu, Title = "Fan not detected spinning", Detail = $"CPU reached {c2:0} °C while the fan reported {fan.Max:0} RPM. Check the fan works (listen for it under load)." });
    }

    /// <summary>On laptops only a drop below the CPU's base clock under load is a real problem.</summary>
    private static void LaptopClocks(RunResult r, List<Finding> f)
    {
        int baseClock = r.Hardware?.Cpu.MaxClockMHz ?? 0;
        if (baseClock <= 0) return;
        var clock = r.Timeline.Series(KeyMetrics.ChannelId(KeyMetrics.CpuClock)).ToList();
        foreach (var step in r.Steps.Where(s => s.Kind == StepKind.Stress && s.Components.Contains(Component.Cpu) && s.End != null))
        {
            double t0 = (step.Start - r.StartTime).TotalSeconds + 30, t1 = (step.End!.Value - r.StartTime).TotalSeconds;
            var win = clock.Where(p => p.T >= t0 && p.T <= t1).Select(p => p.V).ToList();
            if (win.Count < 10) continue;
            double avg = win.Average();
            if (avg < baseClock * 0.8)
            {
                f.Add(new Finding { Severity = Severity.Warning, Component = Component.Cpu, Title = $"CPU fell below its base clock under load ({avg:0} MHz vs {baseClock} MHz base)", Detail = "Running below base speed means the laptop is throttling hard — heat, a weak/unoriginal charger, or a quiet/battery-saver power mode." });
                break;
            }
        }
    }

    /// <summary>
    /// Cooling result: how much of its early peak performance the CPU keeps during a sustained stress step
    /// (uses BenchForge's own throughput measurement, so it works even without temperature sensors).
    /// </summary>
    private static void Cooling(RunResult r, List<Finding> f, bool laptop)
    {
        var series = r.Timeline.Series("test/cpu.stress").ToList();
        if (series.Count < 30) return;
        double? best = null;
        foreach (var step in r.Steps.Where(s => s.Kind == StepKind.Stress && s.Components.Contains(Component.Cpu) && s.End != null))
        {
            double start = (step.Start - r.StartTime).TotalSeconds, end = (step.End!.Value - r.StartTime).TotalSeconds;
            if (end - start < 60) continue;
            var win = series.Where(p => p.T >= start + 3 && p.T <= end - 1).ToList();
            if (win.Count < 30) continue;
            var early = win.Where(p => p.T <= start + 18).Select(p => p.V).ToList();
            var late = win.Skip((int)(win.Count * 0.7)).Select(p => p.V).ToList();
            if (early.Count < 5 || late.Count < 5) continue;
            double ratio = SustainedRatio(early, late);
            best = best == null ? ratio : Math.Min(best.Value, ratio);
            var module = step.Modules.FirstOrDefault(m => m.Component == Component.Cpu);
            module?.SubScores.Add(new SubScore { Name = "Sustained performance (cooling)", Group = "Stress", Value = Math.Round(ratio * 100, 1), Unit = "% of peak" });
        }
        if (best is not double b) return;
        var (sev, verdict, detail) = CoolingVerdict(b, laptop);
        f.Add(new Finding
        {
            Severity = sev, Component = Component.Cpu,
            Title = $"Cooling: {verdict} — CPU keeps {b:P0} of its peak performance under sustained load",
            Detail = detail,
        });
    }

    public static double SustainedRatio(IReadOnlyList<double> early, IReadOnlyList<double> late)
    {
        // Peak = average of the best half of the early samples (ignores ramp-up noise).
        double peak = early.OrderByDescending(x => x).Take(Math.Max(1, early.Count / 2)).Average();
        return peak <= 0 ? 0 : Math.Min(1.05, late.Average() / peak);
    }

    public static (Severity Severity, string Verdict, string Detail) CoolingVerdict(double ratio, bool laptop) => ratio switch
    {
        >= 0.90 => (Severity.Info, "Excellent", "Performance barely drops when hot — the cooling keeps up."),
        >= 0.80 => (Severity.Info, laptop ? "Good (typical laptop)" : "Good", laptop ? "A small drop is normal for laptops as they settle at their power/temperature limit." : "A small drop as the PC heats up."),
        >= 0.65 => (Severity.Warning, "Weak — cooling is struggling", "The CPU loses a noticeable amount of speed once hot. Cleaning dust, better airflow or a performance power mode usually helps."),
        _ => (Severity.Critical, "Poor — heavy thermal throttling", "The CPU loses a lot of speed under sustained load. The cooling system is likely clogged, the fan weak, or the thermal paste dried out."),
    };

    private static double ShareAtOrAbove(RunResult r, string key, double limit)
    {
        var s = r.Timeline.Series(KeyMetrics.ChannelId(key)).Select(p => p.V).ToList();
        return s.Count == 0 ? 0 : s.Count(v => v >= limit) / (double)s.Count;
    }

    private static void PowerAndBattery(RunResult r, List<Finding> f)
    {
        var ac = r.Timeline.Series(KeyMetrics.ChannelId(KeyMetrics.OnAc)).ToList();
        if (ac.Count > 0)
        {
            int off = ac.Count(p => p.V < 0.5);
            if (off == ac.Count)
                f.Add(new Finding { Severity = Severity.Warning, Title = "The whole test ran on battery", Detail = "On battery the PC is power-limited — scores and stress results are lower than with the charger plugged in." });
            else if (off > 0)
            {
                var first = ac.First(p => p.V < 0.5);
                f.Add(new Finding { Severity = Severity.Warning, Title = $"Charger was disconnected {TimeFormat.Short(TimeSpan.FromSeconds(first.T))} into the test", Detail = "Results after that point are power-limited. If you didn't unplug it, check the charger, cable and laptop power socket." });
            }
        }
        if (r.Hardware is { BatteryWearPercent: double wear } && wear >= 30)
            f.Add(new Finding
            {
                Severity = wear >= 50 ? Severity.Warning : Severity.Info,
                Title = $"Battery holds only {100 - wear:0}% of its original charge",
                Detail = $"Design {r.Hardware.BatteryDesignCapacityWh:0.#} Wh, now {r.Hardware.BatteryFullChargeWh:0.#} Wh." + (wear >= 50 ? " A worn battery can also cause sudden shut-offs under heavy load." : ""),
            });
    }

    private static void Temperatures(RunResult r, List<Finding> f)
    {
        if (Max(r, KeyMetrics.CpuTemp) is double ct)
        {
            if (ct >= 95) f.Add(new Finding { Severity = Severity.Critical, Component = Component.Cpu, Title = $"CPU overheating ({ct:0} °C)", Detail = "The CPU hit its thermal limit. It will throttle hard and long-term stability suffers." });
            else if (ct >= 85) f.Add(new Finding { Severity = Severity.Warning, Component = Component.Cpu, Title = $"CPU runs hot ({ct:0} °C)", Detail = "Under full load the CPU got close to its limit. Better cooling would give more headroom." });
            else f.Add(new Finding { Severity = Severity.Info, Component = Component.Cpu, Title = $"CPU temperature OK (max {ct:0} °C)" });
        }
        if (Max(r, KeyMetrics.GpuTemp) is double gt)
        {
            if (gt >= 88) f.Add(new Finding { Severity = Severity.Warning, Component = Component.Gpu, Title = $"GPU runs hot ({gt:0} °C)", Detail = "The graphics card is near its throttle point. Check case airflow, fan curve and dust." });
            else f.Add(new Finding { Severity = Severity.Info, Component = Component.Gpu, Title = $"GPU temperature OK (max {gt:0} °C)" });
        }
        if (Max(r, KeyMetrics.GpuHotspot) is double hs && hs >= 105)
            f.Add(new Finding { Severity = Severity.Critical, Component = Component.Gpu, Title = $"GPU hot spot very high ({hs:0} °C)", Detail = "A large gap between core and hot-spot temperature often means dried-out thermal paste or poor cooler contact." });
        if (Max(r, KeyMetrics.GpuMemTemp) is double mt && mt >= 100)
            f.Add(new Finding { Severity = Severity.Warning, Component = Component.Gpu, Title = $"GPU memory very hot ({mt:0} °C)", Detail = "VRAM above 100 °C can cause artifacts and crashes. Thermal pads may need replacing." });
        if (Max(r, KeyMetrics.DiskTemp) is double dt && dt >= 70)
            f.Add(new Finding { Severity = Severity.Warning, Component = Component.Storage, Title = $"Drive runs hot ({dt:0} °C)", Detail = "NVMe drives throttle around 70-80 °C. A heatsink or airflow over the drive helps." });
        if (Max(r, KeyMetrics.CpuTemp) is double c2 && c2 > 75 && r.KeyStats.TryGetValue(KeyMetrics.CpuFan, out var fan) && fan.Max < 100)
            f.Add(new Finding { Severity = Severity.Critical, Component = Component.Cpu, Title = "CPU fan not detected spinning", Detail = $"CPU reached {c2:0} °C while the CPU fan header reported {fan.Max:0} RPM. Check the fan is connected to CPU_FAN and spinning (pumps may use AIO_PUMP)." });
    }

    /// <summary>Clock drop between the start and end of each stress step while hot = thermal throttling.</summary>
    private static void Throttling(RunResult r, List<Finding> f)
    {
        foreach (var (comp, clockKey, tempKey, hotLimit) in new[] { (Component.Cpu, KeyMetrics.CpuClock, KeyMetrics.CpuTemp, 85.0), (Component.Gpu, KeyMetrics.GpuClock, KeyMetrics.GpuTemp, 80.0) })
        {
            var clock = r.Timeline.Series(KeyMetrics.ChannelId(clockKey)).ToList();
            var temp = r.Timeline.Series(KeyMetrics.ChannelId(tempKey)).ToList();
            if (clock.Count < 20) continue;
            foreach (var step in r.Steps.Where(s => s.Kind == StepKind.Stress && s.Components.Contains(comp) && s.End != null))
            {
                double t0 = (step.Start - r.StartTime).TotalSeconds + 10, t1 = (step.End!.Value - r.StartTime).TotalSeconds - 2;
                var win = clock.Where(p => p.T >= t0 && p.T <= t1).ToList();
                if (win.Count < 12) continue;
                int q = Math.Max(3, win.Count / 5);
                double early = win.Take(q).Average(p => p.V), late = win.Skip(win.Count - q).Average(p => p.V);
                double drop = 1 - late / Math.Max(1, early);
                double maxTemp = temp.Where(p => p.T >= t0 && p.T <= t1).Select(p => p.V).DefaultIfEmpty(0).Max();
                if (drop >= 0.08 && early > 500)
                {
                    bool thermal = maxTemp >= hotLimit;
                    f.Add(new Finding
                    {
                        Severity = thermal ? Severity.Warning : Severity.Info, Component = comp,
                        Title = $"{comp.Display()} {(thermal ? "thermal throttling" : "clock drop")} during \"{step.Name}\"",
                        Detail = $"Clock fell from {early:0} MHz to {late:0} MHz ({drop:P0}) while temperature peaked at {maxTemp:0} °C." +
                                 (thermal ? " The part is slowing itself down to stay cool." : " Probably a power limit rather than heat."),
                    });
                    break;
                }
            }
        }
        // Throughput fall-off reported by the CPU stress itself.
        foreach (var m in r.AllModules.Where(m => m.Component == Component.Cpu && m.Kind == StepKind.Stress))
        {
            var drop = m.SubScores.FirstOrDefault(s => s.Name == "Throughput drop over test");
            if (drop != null && drop.Value >= 10 && !f.Any(x => x.Component == Component.Cpu && (x.Title.Contains("throttl") || x.Title.StartsWith("Cooling:"))))
                f.Add(new Finding { Severity = Severity.Warning, Component = Component.Cpu, Title = $"CPU performance dropped {drop.Value:0}% during the stress test", Detail = "Sustained performance fell over time — typically heat or power limits (throttling)." });
        }
    }

    private static void Errors(RunResult r, List<Finding> f)
    {
        foreach (var g in r.AllModules.Where(m => m.ErrorCount > 0 || m.Failed).GroupBy(m => m.Component))
        {
            int errors = g.Sum(m => m.ErrorCount);
            var info = string.Join(" ", g.SelectMany(m => m.Info).Where(kv => kv.Key.StartsWith("Errors") || kv.Key.StartsWith("Failing") || kv.Key == "Data integrity").Select(kv => $"{kv.Key}: {kv.Value}."));
            var failed = g.FirstOrDefault(m => m.Failed && m.FailureReason != "Cancelled");
            switch (g.Key)
            {
                case Component.Cpu when errors > 0:
                    f.Add(new Finding { Severity = Severity.Critical, Component = Component.Cpu, Title = $"CPU produced {errors} wrong results", Detail = "A healthy CPU never miscalculates. This means the CPU is unstable (overclock, undervolt, too little voltage, overheating) or faulty. " + info });
                    break;
                case Component.Memory when errors > 0:
                    f.Add(new Finding { Severity = Severity.Critical, Component = Component.Memory, Title = $"RAM errors found ({errors})", Detail = "Data written to RAM came back different. Causes: unstable XMP/EXPO profile, a faulty stick, or poor seating. " + info });
                    break;
                case Component.Storage when errors > 0:
                    f.Add(new Finding { Severity = Severity.Critical, Component = Component.Storage, Title = "Drive returned corrupted data", Detail = "Data read back from the drive did not match what was written. Back up important files now. " + info });
                    break;
                case Component.Gpu when failed != null:
                    f.Add(new Finding { Severity = Severity.Critical, Component = Component.Gpu, Title = "3D test crashed / stopped responding", Detail = failed.FailureReason ?? "" });
                    break;
                case Component.Gpu when errors > 0:
                    f.Add(new Finding { Severity = Severity.Critical, Component = Component.Gpu, Title = $"Video memory errors ({errors})", Detail = "The GPU returned wrong data from its memory — unstable memory overclock, overheating VRAM or a failing card. " + info });
                    break;
                default:
                    if (failed != null) f.Add(new Finding { Severity = Severity.Warning, Component = g.Key, Title = $"{g.Key.Display()} test did not complete", Detail = failed.FailureReason ?? "" });
                    break;
            }
        }
        if (r.AbortReason != null && !r.AbortReason.StartsWith("Stopped by user"))
            f.Add(new Finding { Severity = Severity.Critical, Title = "Run stopped early", Detail = r.AbortReason });
        if (r.Crash != null)
            f.Add(new Finding { Severity = Severity.Critical, Component = r.Crash.SuspectedComponent, Title = r.Crash.Summary, Detail = string.Join(" ", r.Crash.Explanation) });
    }

    private static void Performance(RunResult r, List<Finding> f)
    {
        var hw = r.Hardware;
        var mem = r.AllModules.FirstOrDefault(m => m.Component == Component.Memory && m.Kind == StepKind.Benchmark && !m.Failed);
        if (mem != null && hw != null)
        {
            double read = mem.SubScores.FirstOrDefault(s => s.Name == "Read bandwidth")?.Value ?? 0;
            int sticks = hw.MemoryModules.Count;
            int speed = hw.MemoryModules.Select(m => m.ConfiguredSpeedMTs).DefaultIfEmpty(0).Max();
            if (speed > 0 && read > 0)
            {
                // Theoretical dual-channel bandwidth = speed × 8 bytes × 2 channels.
                double dual = speed * 8.0 * 2;
                double efficiency = read / dual;
                if (sticks == 1 || efficiency < 0.42)
                    f.Add(new Finding { Severity = Severity.Warning, Component = Component.Memory, Title = "RAM may be running in single-channel mode", Detail = $"Read bandwidth {read:0} MB/s is only {efficiency:P0} of what dual-channel {speed} MT/s should give ({sticks} module{(sticks == 1 ? "" : "s")} detected).", Confidence = sticks == 1 ? 0.9 : 0.5 });
            }
        }
        if (hw != null)
        {
            var slow = hw.MemoryModules.Where(m => m.SpeedMTs > 0 && m.ConfiguredSpeedMTs > 0 && m.ConfiguredSpeedMTs < m.SpeedMTs * 0.9).ToList();
            if (slow.Count > 0)
                f.Add(new Finding { Severity = Severity.Info, Component = Component.Memory, Title = $"RAM running at {slow[0].ConfiguredSpeedMTs} MT/s (rated {slow[0].SpeedMTs} MT/s)", Detail = "The memory profile (XMP/EXPO) is not enabled in the BIOS — free performance is available." });
        }
        var disk = r.AllModules.FirstOrDefault(m => m.Component == Component.Storage && m.Kind == StepKind.Benchmark && !m.Failed);
        if (disk != null)
        {
            double seq = disk.SubScores.FirstOrDefault(s => s.Name == "Sequential read")?.Value ?? 0;
            var target = disk.Info.GetValueOrDefault("Target", "");
            var drive = hw?.Disks.FirstOrDefault(d => d.Volumes.Any(v => target.StartsWith(v, StringComparison.OrdinalIgnoreCase)));
            if (drive != null)
            {
                if (drive.BusType.Contains("NVMe", StringComparison.OrdinalIgnoreCase) && seq > 0 && seq < 1200)
                    f.Add(new Finding { Severity = Severity.Warning, Component = Component.Storage, Title = $"NVMe drive slower than expected ({seq:0} MB/s)", Detail = "NVMe drives should read well above 1500 MB/s. Check the M.2 slot (chipset/shared lanes), PCIe generation in BIOS, thermal throttling, and that the drive isn't nearly full." });
                if (drive.MediaType == "SSD" && drive.BusType.Contains("SATA", StringComparison.OrdinalIgnoreCase) && seq > 0 && seq < 350)
                    f.Add(new Finding { Severity = Severity.Warning, Component = Component.Storage, Title = $"SATA SSD slower than expected ({seq:0} MB/s)", Detail = "SATA SSDs normally reach ~500 MB/s. Check AHCI mode, SATA cable/port (use SATA3 6Gb/s ports), and free space." });
            }
        }
        var gpu = r.AllModules.FirstOrDefault(m => m.Component == Component.Gpu && m.Info.TryGetValue("Renderer", out var rend) && (rend.Contains("llvmpipe", StringComparison.OrdinalIgnoreCase) || rend.Contains("Microsoft Basic", StringComparison.OrdinalIgnoreCase) || rend.Contains("GDI Generic", StringComparison.OrdinalIgnoreCase)));
        if (gpu != null)
            f.Add(new Finding { Severity = Severity.Critical, Component = Component.Gpu, Title = "3D test ran on a software renderer", Detail = $"Renderer was '{gpu.Info["Renderer"]}', not your graphics card. The GPU driver is missing or broken — install the latest driver from NVIDIA/AMD/Intel." });
        var intel = r.AllModules.FirstOrDefault(m => m.Component == Component.Gpu && m.Info.TryGetValue("Renderer", out var rend) && rend.Contains("Intel", StringComparison.OrdinalIgnoreCase));
        if (intel != null && hw != null && hw.Gpus.Count > 1 && hw.Gpus.Any(g => !g.Name.Contains("Intel", StringComparison.OrdinalIgnoreCase)))
            f.Add(new Finding { Severity = Severity.Warning, Component = Component.Gpu, Title = "3D test ran on the integrated GPU", Detail = "A dedicated graphics card is installed but the test used Intel graphics. On laptops, set BenchForge to 'High performance' in Windows Settings › Display › Graphics; on desktops plug the monitor into the graphics card." });
    }

    private static void Power(RunResult r, List<Finding> f)
    {
        if (r.KeyStats.TryGetValue(KeyMetrics.Rail12V, out var v12) && v12.Min > 5 && v12.Min < 11.4)
            f.Add(new Finding { Severity = Severity.Warning, Title = $"+12V rail dropped to {v12.Min:0.00} V", Detail = "ATX spec minimum is 11.4 V. Motherboard readings are approximate, but a low reading under load can mean a weak power supply." });
        if (r.KeyStats.TryGetValue(KeyMetrics.CpuPower, out var cp) && r.KeyStats.TryGetValue(KeyMetrics.GpuPower, out var gp))
            f.Add(new Finding { Severity = Severity.Info, Title = $"Peak CPU + GPU power ≈ {cp.Max + gp.Max:0} W", Detail = $"CPU {cp.Max:0} W + GPU {gp.Max:0} W (plus ~50-100 W for the rest of the system). Your PSU should be rated comfortably above this." });
    }
}
