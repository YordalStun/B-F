using BenchForge.Core.Crash;
using BenchForge.Core.Model;

namespace BenchForge.Core.Analysis;

/// <summary>Turns findings, errors, crash data and hardware info into concrete things to try, most important first.</summary>
public static class RecommendationEngine
{
    public static List<Recommendation> Build(RunResult r)
    {
        var list = new List<Recommendation>();
        void Add(Severity s, Component? c, string title, string detail, params string[] actions)
        {
            if (list.Any(x => x.Title == title)) return;
            list.Add(new Recommendation { Severity = s, Component = c, Title = title, Detail = detail, Actions = actions.ToList() });
        }

        var f = r.Findings;
        bool Has(Func<Finding, bool> p) => f.Any(p);

        if (Has(x => x.Component == Component.Cpu && x.Title.Contains("wrong results")))
            Add(Severity.Critical, Component.Cpu, "Make the CPU stable", "The CPU made calculation errors under load.",
                "Load BIOS defaults: remove CPU overclocks, undervolts, PBO/Curve Optimizer or Intel 'Turbo' tweaks",
                "Update the BIOS (many 13th/14th-gen Intel and Ryzen stability fixes ship as BIOS updates)",
                "If errors were on one logical CPU only: raise that core's Curve Optimizer value (less negative) or lower its boost",
                "Check CPU temperatures and cooler mounting",
                "Re-run 'CPU stress (Mixed)' for 1 hour — it must finish with 0 errors");
        if (Has(x => x.Component == Component.Memory && x.Title.StartsWith("RAM errors")))
            Add(Severity.Critical, Component.Memory, "Fix RAM errors", "RAM errors cause random crashes, corrupted files and blue screens.",
                "Disable XMP/EXPO in BIOS and re-test — if errors stop, the profile is unstable on your board/CPU",
                "Keep XMP but lower the speed one step, or raise DRAM voltage +0.02 V / SoC (AMD) or VCCSA (Intel) slightly",
                "Test one stick at a time in the slot the manual recommends to find a faulty stick",
                "Reseat the RAM and clean the slots",
                "Run Windows Memory Diagnostic or MemTest86 overnight for confirmation");
        if (Has(x => x.Component == Component.Gpu && (x.Title.Contains("crashed") || x.Title.Contains("Video memory"))))
            Add(Severity.Critical, Component.Gpu, "Stabilise the graphics card", "The GPU crashed, stopped responding or returned wrong data.",
                "Remove any GPU overclock / undervolt (MSI Afterburner, Adrenalin tuning) and memory overclock",
                "Clean-install the latest driver (use DDU in Safe Mode, then install fresh)",
                "Use two separate PCIe power cables from the PSU (no daisy-chained pigtail)",
                "Check GPU temperatures and clean the cooler; check VRAM temperature",
                "If it still fails at stock settings, test the card in another PC or contact the manufacturer (RMA)");
        if (Has(x => x.Component == Component.Storage && x.Severity == Severity.Critical))
            Add(Severity.Critical, Component.Storage, "Protect your data — drive problem", "The drive returned corrupted data or reported errors.",
                "Back up important files immediately",
                "Check drive health with the drive maker's tool (Samsung Magician, WD Dashboard, Crucial Storage Executive) or CrystalDiskInfo",
                "Update the SSD firmware",
                "For SATA drives replace the SATA cable and try another port",
                "Run 'chkdsk /scan' from an admin command prompt");
        if (Has(x => x.Title.Contains("overheating") || x.Title.Contains("runs hot") || x.Title.Contains("thermal throttling") || x.Title.Contains("hot spot")))
        {
            bool cpu = Has(x => x.Component == Component.Cpu && (x.Title.Contains("overheating") || x.Title.Contains("hot") || x.Title.Contains("throttl")));
            bool gpu = Has(x => x.Component == Component.Gpu && (x.Title.Contains("hot") || x.Title.Contains("throttl")));
            var actions = new List<string>();
            if (cpu) actions.AddRange(new[] { "Check the CPU cooler is mounted firmly and its fan/pump is running", "Re-apply thermal paste if the PC is a few years old", "Set a more aggressive fan curve in the BIOS", "Consider lowering the CPU power limit (PL1/PL2 or PPT) by 10-20% — very small performance loss, big temperature drop" });
            if (gpu) actions.AddRange(new[] { "Clean dust from the GPU fans and heatsink", "Increase case airflow (front intake / rear exhaust)", "Set a custom GPU fan curve or slightly undervolt the GPU" });
            actions.Add("Make sure case fans are running and filters are clean");
            Add(Severity.Warning, cpu && !gpu ? Component.Cpu : gpu && !cpu ? Component.Gpu : null, "Improve cooling", "Parts reached temperatures where they slow down or become unstable.", actions.ToArray());
        }
        if (Has(x => x.Title.Contains("CPU fan not detected")))
            Add(Severity.Critical, Component.Cpu, "Check the CPU fan", "The CPU fan reported 0 RPM while the CPU was hot.", "Check the fan cable is on the CPU_FAN header", "Make sure the fan/pump actually spins", "Enable fan-failure warning in BIOS");
        if (Has(x => x.Title.Contains("single-channel")))
            Add(Severity.Warning, Component.Memory, "Run RAM in dual-channel", "Memory bandwidth is roughly half of what it should be.",
                "Install RAM in matching pairs", "With two sticks, use the slots the manual marks for dual-channel (usually A2 + B2)", "Reseat the sticks — a stick not detected properly drops to single-channel");
        if (Has(x => x.Title.Contains("rated")))
            Add(Severity.Info, Component.Memory, "Enable XMP / EXPO", "Your RAM is running slower than it is rated for.",
                "In BIOS enable XMP (Intel) or EXPO/DOCP (AMD)", "Run the RAM error test afterwards to confirm it is stable");
        if (Has(x => x.Title.Contains("software renderer") || x.Title.Contains("integrated GPU")))
            Add(Severity.Critical, Component.Gpu, "Use the real graphics card", "The 3D test didn't run on your dedicated GPU.",
                "Install the latest NVIDIA / AMD / Intel graphics driver", "Plug the monitor into the graphics card, not the motherboard", "Laptop: Windows Settings › System › Display › Graphics › BenchForge › High performance");
        if (Has(x => x.Title.Contains("NVMe drive slower") || x.Title.Contains("SATA SSD slower")))
            Add(Severity.Warning, Component.Storage, "Check the drive connection", "The drive is slower than its type should be.",
                "Make sure the drive is not nearly full (keep 15%+ free)", "NVMe: use the CPU-connected M.2 slot and check PCIe generation in BIOS", "SATA: enable AHCI mode, use a SATA 6 Gb/s port", "Update storage drivers and SSD firmware");
        if (Has(x => x.Title.Contains("+12V")))
            Add(Severity.Warning, null, "Check the power supply", "The 12 V rail dipped below spec under load.", "Verify the reading with a multimeter if possible", "Make sure the PSU is a quality unit rated for your parts with headroom");

        // Crash-specific advice.
        if (r.Crash is CrashInfo c)
        {
            uint? code = c.Signals.FirstOrDefault(s => s.BugCheckCode is > 0)?.BugCheckCode ?? c.Dumps.FirstOrDefault(d => d.Valid)?.BugCheckCode;
            if (code is uint bc && BugCheckKnowledgeBase.Get(bc) is BugCheckInfo kb && kb.Fixes.Length > 0)
                Add(Severity.Critical, kb.LikelyComponent, $"Fix for {kb.Name}", kb.Meaning, kb.Fixes);
            if (c.Kind == CrashKind.PowerLossOrHang)
            {
                double p = c.PeakMetrics.GetValueOrDefault(KeyMetrics.CpuPower) + c.PeakMetrics.GetValueOrDefault(KeyMetrics.GpuPower);
                Add(Severity.Critical, null, "Find out why the PC shut off", "There was no blue screen — the PC froze or lost power.",
                    p > 350 ? $"Load was high (~{p:0} W CPU+GPU): suspect the power supply first — check its rating/age, cables, and try a different outlet (no power strips)" : "Check for overheating and loose power cables (24-pin, CPU 8-pin, GPU)",
                    "Run each stress test on its own (Stability rotation) to see which part triggers it",
                    "Undo any overclock/undervolt — some unstable settings freeze instead of blue-screening",
                    "Update the BIOS");
            }
            if (c.Signals.Any(s => s.Kind == SignalKind.WheaFatal || s.Kind == SignalKind.WheaCorrected))
                Add(Severity.Critical, c.SuspectedComponent, "Hardware errors were reported (WHEA)", "The hardware itself flagged errors around the crash.",
                    "Reset BIOS to defaults and update it", "If the WHEA source is 'PCI Express': reseat the graphics card/NVMe and set the slot to a lower PCIe generation (e.g. Gen 3) in BIOS", "If the source is a processor core: remove CPU overclock/undervolt; if it persists at stock, the CPU may be faulty");
            if (c.Kind == CrashKind.AppTerminated)
                Add(Severity.Info, null, "BenchForge was closed unexpectedly", "Windows itself did not crash.", "If you did not close it, check antivirus logs (some flag stress tests)", "Run the test again — if it repeats, export the result and share it");
        }

        if (r.Hardware is HardwareInventory hw)
        {
            if (hw.BiosDate is DateTime bd && bd < DateTime.Now.AddYears(-3))
                Add(Severity.Info, null, "Consider a BIOS update", $"BIOS is from {bd:yyyy-MM}. Newer versions often fix stability, memory compatibility and CPU issues.", "Download the latest BIOS from the motherboard maker's support page for your exact model", "Follow their update instructions carefully");
            foreach (var g in hw.Gpus.Where(g => g.DriverDate is DateTime dd && dd < DateTime.Now.AddYears(-1) && !g.Name.Contains("Basic", StringComparison.OrdinalIgnoreCase)))
                Add(Severity.Info, Component.Gpu, "Update the graphics driver", $"{g.Name} driver is from {g.DriverDate:yyyy-MM}.", "Get the latest driver from NVIDIA, AMD or Intel");
            foreach (var d in hw.Disks.Where(d => d.SmartPredictFailure == true || d.Health.Equals("Unhealthy", StringComparison.OrdinalIgnoreCase) || d.Health.Equals("Warning", StringComparison.OrdinalIgnoreCase)))
                Add(Severity.Critical, Component.Storage, $"Drive health warning: {d.Model}", $"Windows reports this drive as '{d.Health}'{(d.SmartPredictFailure == true ? " and SMART predicts failure" : "")}.", "Back up everything on it now", "Plan to replace the drive");
            foreach (var d in hw.Disks.Where(d => d.WearPercent is > 80))
                Add(Severity.Warning, Component.Storage, $"SSD nearly worn out: {d.Model}", $"Wear level {d.WearPercent:0}%.", "Back up and plan a replacement");
            foreach (var d in hw.Disks.Where(d => d.FreePercent is < 10))
                Add(Severity.Info, Component.Storage, $"Low free space on {string.Join(", ", d.Volumes)}", $"Only {d.FreePercent:0}% free — SSDs slow down and Windows updates fail when nearly full.", "Free up space or move large files");
            if (hw.PowerPlan.Contains("saver", StringComparison.OrdinalIgnoreCase))
                Add(Severity.Info, null, "Power saver plan is active", "Performance is being limited by Windows power settings.", "Switch to 'Balanced' or 'High performance' in Control Panel › Power Options");
        }

        if (list.Count == 0 && r.Status == RunStatus.Completed)
            Add(Severity.Info, null, "No problems found", "Everything finished without errors and temperatures stayed in a safe range.", "For a deeper stability check, run the Burn test for 1–2 hours or the Overnight test");

        return list.OrderByDescending(x => x.Severity).ToList();
    }
}
