using BenchForge.Core.Model;

namespace BenchForge.Core.Engine;

public interface ITestModule
{
    Component Component { get; }
    Task<ModuleResult> RunAsync(StepContext ctx);
}

public interface IModuleFactory
{
    ITestModule Create(Component component, StepKind kind);
}

/// <summary>Everything a test module needs while running one step.</summary>
public sealed class StepContext
{
    private readonly Action<Component, string, double> _progress;
    private readonly Action<TestError> _error;
    private readonly Action<string, string, double, string> _metric;
    private readonly Action<string, Severity> _log;

    public StepContext(TestStep step, TestSettings settings, CancellationToken token, DateTime start, Component component,
        Action<Component, string, double> progress, Action<TestError> error, Action<string, string, double, string> metric, Action<string, Severity> log)
    {
        Step = step; Settings = settings; Token = token; Start = start; Component = component;
        _progress = progress; _error = error; _metric = metric; _log = log;
    }

    public TestStep Step { get; }
    public TestSettings Settings { get; }
    public CancellationToken Token { get; }
    public DateTime Start { get; }
    public Component Component { get; }
    public StepKind Kind => Step.Kind;
    public double DurationSeconds => Step.DurationSeconds;
    /// <summary>True when other components are being stressed at the same time.</summary>
    public bool Concurrent => Step.Components.Count > 1;

    public TimeSpan Elapsed => DateTime.Now - Start;
    public TimeSpan Remaining => TimeSpan.FromSeconds(DurationSeconds) - Elapsed;
    public DateTime Deadline => Start.AddSeconds(DurationSeconds);

    public void Progress(string phase, double fraction) => _progress(Component, phase, Math.Clamp(fraction, 0, 1));
    public void Error(TestError e) { e.Component = Component; _error(e); }
    /// <summary>Live test metric (FPS, MB/s, ops/s) recorded into the sensor timeline.</summary>
    public void Metric(string id, string name, double value, string unit) => _metric(id, name, value, unit);
    public void Log(string message, Severity severity = Severity.Info) => _log($"[{Component.Display()}] {message}", severity);
}

public sealed class RunProgress
{
    public int StepIndex { get; init; }
    public int StepCount { get; init; }
    public string StepName { get; init; } = "";
    public StepKind StepKind { get; init; }
    public IReadOnlyList<Component> Components { get; init; } = Array.Empty<Component>();
    public double StepFraction { get; init; }
    public double OverallFraction { get; init; }
    public TimeSpan Elapsed { get; init; }
    public TimeSpan Remaining { get; init; }
    public TimeSpan StepRemaining { get; init; }
    public DateTime EstimatedEnd { get; init; }
    public IReadOnlyDictionary<Component, (string Phase, double Fraction)> Phases { get; init; } = new Dictionary<Component, (string, double)>();
    public int ErrorCount { get; init; }
}
