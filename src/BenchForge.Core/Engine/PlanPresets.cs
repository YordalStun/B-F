using BenchForge.Core.Model;

namespace BenchForge.Core.Engine;

public static class PlanPresets
{
    private static readonly Component[] AllParts = { Component.Cpu, Component.Memory, Component.Storage, Component.Gpu };

    public static TestPlan QuickBenchmark(TestSettings? s = null) => new()
    {
        Name = "Quick benchmark",
        Description = "Scores every part in about 4 minutes.",
        Settings = s ?? new(),
        Steps =
        {
            new("CPU benchmark", StepKind.Benchmark, 62, Component.Cpu),
            new("RAM benchmark", StepKind.Benchmark, 23, Component.Memory),
            new("Storage benchmark", StepKind.Benchmark, 38, Component.Storage),
            new("GPU 3D benchmark", StepKind.Benchmark, 75, Component.Gpu),
        },
    };

    public static TestPlan FullBenchmark(TestSettings? s = null) => new()
    {
        Name = "Full benchmark",
        Description = "Longer, more accurate scores plus a short stability check of each part (~15 min).",
        Settings = s ?? new(),
        Steps =
        {
            new("CPU benchmark", StepKind.Benchmark, 146, Component.Cpu),
            new("RAM benchmark", StepKind.Benchmark, 43, Component.Memory),
            new("Storage benchmark", StepKind.Benchmark, 92, Component.Storage),
            new("GPU 3D benchmark", StepKind.Benchmark, 163, Component.Gpu),
            new("CPU stability", StepKind.Stress, 120, Component.Cpu),
            new("RAM error test", StepKind.Stress, 180, Component.Memory),
            new("GPU stability + VRAM check", StepKind.Stress, 120, Component.Gpu),
            new("Everything at once", StepKind.Stress, 120, Component.Cpu, Component.Gpu, Component.Memory),
        },
    };

    /// <summary>All selected components stressed simultaneously for the whole duration (checkpoints every 15 minutes for crash analysis).</summary>
    public static TestPlan BurnIn(TimeSpan duration, IEnumerable<Component> parts, TestSettings? s = null)
    {
        var comps = parts.Distinct().ToArray();
        if (comps.Length == 0) comps = new[] { Component.Cpu, Component.Gpu, Component.Memory };
        var plan = new TestPlan
        {
            Name = $"Burn test {TimeFormat.Short(duration)} ({string.Join(" + ", comps.Select(c => c.Display()))})",
            Description = "Stresses the selected parts at the same time for the whole duration.",
            Settings = s ?? new(),
        };
        double remaining = duration.TotalSeconds;
        int n = 1;
        while (remaining > 1)
        {
            double chunk = Math.Min(900, remaining);
            plan.Steps.Add(new TestStep($"Burn {n++}", StepKind.Stress, chunk, comps));
            remaining -= chunk;
        }
        return plan;
    }

    /// <summary>Rotates each part on its own, then everything together, until the time is used up. Best for finding which part is unstable.</summary>
    public static TestPlan StabilityRotation(TimeSpan duration, TestSettings? s = null)
    {
        var plan = new TestPlan { Name = $"Stability rotation {TimeFormat.Short(duration)}", Description = "Each part alone, then everything together — repeats until the time is up.", Settings = s ?? new() };
        var cycle = new (string Name, Component[] Parts)[]
        {
            ("CPU stress", new[] { Component.Cpu }),
            ("RAM error test", new[] { Component.Memory }),
            ("GPU stress", new[] { Component.Gpu }),
            ("Storage write/verify", new[] { Component.Storage }),
            ("CPU + GPU + RAM", new[] { Component.Cpu, Component.Gpu, Component.Memory }),
        };
        double remaining = duration.TotalSeconds;
        double slot = Math.Clamp(duration.TotalSeconds / 10, 60, 600);
        int i = 0;
        while (remaining > 1)
        {
            var (name, parts) = cycle[i % cycle.Length];
            double len = Math.Min(slot, remaining);
            plan.Steps.Add(new TestStep(name, StepKind.Stress, len, parts));
            remaining -= len;
            i++;
        }
        return plan;
    }

    /// <summary>Benchmark → long stability rotation → benchmark again (shows if performance drops when hot).</summary>
    public static TestPlan Overnight(TimeSpan stressDuration, TestSettings? s = null)
    {
        var quick = QuickBenchmark(s);
        var plan = new TestPlan { Name = $"Overnight ({TimeFormat.Short(stressDuration)} stability)", Description = "Benchmark, long stability rotation, then benchmark again to compare.", Settings = s ?? new() };
        plan.Steps.AddRange(quick.Steps.Select(x => new TestStep(x.Name + " (before)", x.Kind, x.DurationSeconds, x.Components.ToArray())));
        // A share of the night goes to the core cycler, which finds single-core instability the rotation can't.
        double cycle = Math.Clamp(stressDuration.TotalSeconds * 0.2, 0, 3 * 3600);
        if (cycle >= 600) plan.Steps.Add(new TestStep(StepVariants.CoreCycleName, StepKind.Stress, cycle, Component.Cpu) { Variant = StepVariants.CoreCycle });
        plan.Steps.AddRange(StabilityRotation(stressDuration - TimeSpan.FromSeconds(cycle >= 600 ? cycle : 0), s).Steps);
        plan.Steps.AddRange(quick.Steps.Select(x => new TestStep(x.Name + " (after)", x.Kind, x.DurationSeconds, x.Components.ToArray())));
        return plan;
    }

    /// <summary>For each chosen drive: benchmark, then surface scan of the whole disk + sustained-write cache test.</summary>
    public static TestPlan DriveHealthCheck(TimeSpan perDrive, IReadOnlyList<string> drives, TestSettings? s = null)
    {
        s ??= new();
        if (drives.Count == 0) drives = new[] { Storage.StorageModule.DefaultTarget() };
        var plan = new TestPlan { Name = $"Drive health check ({string.Join(", ", drives.Select(d => d.TrimEnd('\\', '/')))})", Description = "Benchmarks each drive, reads the whole disk looking for bad or slow areas and measures the fast write cache.", Settings = s };
        double health = Math.Max(120, perDrive.TotalSeconds - 92);
        foreach (var d in drives)
        {
            var label = d.TrimEnd('\\', '/');
            plan.Steps.Add(new TestStep($"Drive benchmark {label}", StepKind.Benchmark, 92, Component.Storage) { Target = d });
            plan.Steps.Add(new TestStep($"Drive health check {label}", StepKind.Stress, health, Component.Storage) { Target = d, Variant = StepVariants.DriveHealth });
        }
        return plan;
    }

    /// <summary>
    /// Tests that catch crashes full-load tests miss: single cores at max boost, idle/light load, and power-supply load spikes.
    /// The time is shared out between the chosen tests.
    /// </summary>
    public static TestPlan CrashFinder(TimeSpan duration, bool coreCycle, bool lightLoad, bool psuSpikes, TestSettings? s = null)
    {
        var parts = new List<(string Name, string Variant, double Weight, Component[] Comps)>();
        if (coreCycle) parts.Add((StepVariants.CoreCycleName, StepVariants.CoreCycle, 2, new[] { Component.Cpu }));
        if (lightLoad) parts.Add((StepVariants.LightLoadName, StepVariants.LightLoad, 1, new[] { Component.Cpu }));
        if (psuSpikes) parts.Add((StepVariants.PsuSpikeName, StepVariants.PsuSpike, 1, new[] { Component.Cpu, Component.Gpu }));
        var plan = new TestPlan { Name = $"Crash finder {TimeFormat.Short(duration)}", Description = "Single-core boost, idle / light load and power-supply load spikes.", Settings = s ?? new() };
        double weights = parts.Sum(p => p.Weight);
        foreach (var (name, variant, weight, comps) in parts)
            plan.Steps.Add(new TestStep(name, StepKind.Stress, Math.Max(60, duration.TotalSeconds * weight / weights), comps) { Variant = variant });
        return plan;
    }

    public static TestPlan SingleComponent(Component c, StepKind kind, TimeSpan duration, TestSettings? s = null) => new()
    {
        Name = $"{c.Display()} {(kind == StepKind.Benchmark ? "benchmark" : "stress")}",
        Settings = s ?? new(),
        Steps = { new($"{c.Display()} {(kind == StepKind.Benchmark ? "benchmark" : "stress")}", kind, duration.TotalSeconds, c) },
    };

    public static IReadOnlyList<Component> Everything => AllParts;
}
