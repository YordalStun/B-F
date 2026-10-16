using System.Text.Json.Serialization;

namespace BenchForge.Core.Model;

public sealed class SubScore
{
    public string Name { get; set; } = "";
    /// <summary>Grouping, e.g. "Single-thread" / "Multi-thread" / "Sequential".</summary>
    public string Group { get; set; } = "";
    public double Value { get; set; }
    public string Unit { get; set; } = "";
    public double Score { get; set; }
    public bool HigherIsBetter { get; set; } = true;
    public string? Detail { get; set; }
}

public sealed class TestError
{
    public DateTime Time { get; set; } = DateTime.Now;
    public Component Component { get; set; }
    public string Test { get; set; } = "";
    public string Message { get; set; } = "";
    public int? Thread { get; set; }
    public string? Detail { get; set; }
}

public sealed class ModuleResult
{
    public Component Component { get; set; }
    public StepKind Kind { get; set; }
    public List<SubScore> SubScores { get; set; } = new();
    public double? Score { get; set; }
    public int ErrorCount { get; set; }
    public bool Failed { get; set; }
    public string? FailureReason { get; set; }
    /// <summary>Extra facts (e.g. "Renderer", "Threads", "Bytes verified").</summary>
    public Dictionary<string, string> Info { get; set; } = new();
    /// <summary>Extra charts produced by the test (drive surface map, sustained-write curve …).</summary>
    public List<ChartData> Charts { get; set; } = new();
}

public sealed class ChartData
{
    public string Title { get; set; } = "";
    public string XLabel { get; set; } = "";
    public string YLabel { get; set; } = "";
    public string Unit { get; set; } = "";
    /// <summary>[x, y] pairs.</summary>
    public List<double[]> Points { get; set; } = new();
    /// <summary>Optional notes drawn under the chart (e.g. "SLC cache ends at 24 GB").</summary>
    public string? Note { get; set; }
}

public sealed class StepResult
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public StepKind Kind { get; set; }
    public List<Component> Components { get; set; } = new();
    /// <summary>Special test flavour (see StepVariants), if any.</summary>
    public string? Variant { get; set; }
    public double PlannedSeconds { get; set; }
    public DateTime Start { get; set; }
    public DateTime? End { get; set; }
    public string Status { get; set; } = "Pending";
    public List<ModuleResult> Modules { get; set; } = new();
}

public sealed class RunLogEntry
{
    public DateTime Time { get; set; } = DateTime.Now;
    public Severity Severity { get; set; }
    public string Message { get; set; } = "";
}

public sealed class Recommendation
{
    public Severity Severity { get; set; }
    public Component? Component { get; set; }
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public List<string> Actions { get; set; } = new();
    /// <summary>One-click fixes BenchForge can apply for this problem.</summary>
    public List<Analysis.FixId> Fixes { get; set; } = new();
    [JsonIgnore] public IEnumerable<Analysis.FixInfo> FixInfos => Fixes.Select(Analysis.Fixes.Get);
}

public sealed class Finding
{
    public Severity Severity { get; set; }
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public Component? Component { get; set; }
    /// <summary>0..1, how sure the analysis is.</summary>
    public double Confidence { get; set; } = 1;
}

/// <summary>Everything about one run. Saved as a portable .bfresult file.</summary>
public sealed class RunResult
{
    public int FormatVersion { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "";
    public string AppVersion { get; set; } = "";
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public RunStatus Status { get; set; } = RunStatus.Running;
    public string MachineName { get; set; } = Environment.MachineName;
    /// <summary>True when the file was imported from another PC.</summary>
    public bool Imported { get; set; }
    public string? ImportedFrom { get; set; }
    public string? Notes { get; set; }

    public TestPlan Plan { get; set; } = new();
    public HardwareInventory? Hardware { get; set; }
    public List<StepResult> Steps { get; set; } = new();

    public Dictionary<Component, double> ComponentScores { get; set; } = new();
    public double? OverallScore { get; set; }

    public SensorTimeline Timeline { get; set; } = new();
    public List<ChannelStats> SensorStats { get; set; } = new();
    /// <summary>Min/avg/max of headline metrics (cpu.temp, gpu.load …) keyed by metric id.</summary>
    public Dictionary<string, ChannelStats> KeyStats { get; set; } = new();

    public List<TestError> Errors { get; set; } = new();
    public int TotalErrorCount { get; set; }
    public List<RunLogEntry> Log { get; set; } = new();
    public string? AbortReason { get; set; }

    public CrashInfo? Crash { get; set; }
    public List<EventEntry> RelatedEvents { get; set; } = new();
    public List<Finding> Findings { get; set; } = new();
    public List<Recommendation> Recommendations { get; set; } = new();

    [JsonIgnore] public TimeSpan Duration => (EndTime ?? DateTime.Now) - StartTime;
    [JsonIgnore] public bool IsFailure => Status is RunStatus.Crashed or RunStatus.CompletedWithErrors || (Status == RunStatus.Aborted && AbortReason != null && !AbortReason.StartsWith("Stopped by user"));
    [JsonIgnore] public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Plan.Name : Name;

    public IEnumerable<ModuleResult> AllModules => Steps.SelectMany(s => s.Modules);
}

/// <summary>Small summary for history lists (avoids loading timelines).</summary>
public sealed class RunSummary
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string PlanName { get; set; } = "";
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public RunStatus Status { get; set; }
    public string MachineName { get; set; } = "";
    public bool Imported { get; set; }
    public double? OverallScore { get; set; }
    public Dictionary<Component, double> ComponentScores { get; set; } = new();
    public int ErrorCount { get; set; }
    public string? CrashSummary { get; set; }
    public double? MaxCpuTemp { get; set; }
    public double? MaxGpuTemp { get; set; }
    public string FilePath { get; set; } = "";

    public static RunSummary From(RunResult r, string path) => new()
    {
        Id = r.Id,
        Name = r.DisplayName,
        PlanName = r.Plan.Name,
        StartTime = r.StartTime,
        EndTime = r.EndTime,
        Status = r.Status,
        MachineName = r.ImportedFrom ?? r.MachineName,
        Imported = r.Imported,
        OverallScore = r.OverallScore,
        ComponentScores = new(r.ComponentScores),
        ErrorCount = r.TotalErrorCount,
        CrashSummary = r.Crash?.Summary,
        MaxCpuTemp = r.KeyStats.TryGetValue(KeyMetrics.CpuTemp, out var c) ? c.Max : null,
        MaxGpuTemp = r.KeyStats.TryGetValue(KeyMetrics.GpuTemp, out var g) ? g.Max : null,
        FilePath = path,
    };
}
