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
        plan.Steps.AddRange(StabilityRotation(stressDuration, s).Steps);
        plan.Steps.AddRange(quick.Steps.Select(x => new TestStep(x.Name + " (after)", x.Kind, x.DurationSeconds, x.Components.ToArray())));
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
