using System.Collections.Concurrent;
using System.Diagnostics;
using BenchForge.Core.Analysis;
using BenchForge.Core.Model;
using BenchForge.Core.Persistence;

namespace BenchForge.Core.Engine;

/// <summary>
/// Runs a <see cref="TestPlan"/>: executes each step's modules concurrently, samples sensors every second,
/// keeps the crash journal up to date, enforces safety limits and produces a <see cref="RunResult"/>.
/// Every step is time-boxed, so the remaining time shown to the user is exact.
/// </summary>
public sealed class PlanRunner
{
    private readonly TestPlan _plan;
    private readonly IModuleFactory _factory;
    private readonly ISensorProvider _sensors;
    private readonly HardwareInventory? _hardware;
    private readonly bool _useJournal;
    private readonly string? _journalRoot;
    private readonly CancellationTokenSource _runCts = new();
    private readonly ConcurrentDictionary<string, (string Name, double Value, string Unit)> _metrics = new();
    private readonly ConcurrentDictionary<Component, (string Phase, double Fraction)> _phases = new();
    private readonly List<TestError> _errors = new();
    private readonly List<RunLogEntry> _log = new();
    private readonly LinkedList<HeartbeatSample> _tail = new();
    private int _errorCount;
    private string? _abortReason;
    private bool _userStopped;

    public PlanRunner(TestPlan plan, IModuleFactory factory, ISensorProvider sensors, HardwareInventory? hardware = null, bool useJournal = true, string? journalRoot = null)
    {
        _plan = plan; _factory = factory; _sensors = sensors; _hardware = hardware; _useJournal = useJournal; _journalRoot = journalRoot;
    }

    public event Action<RunProgress>? ProgressChanged;
    public event Action<SensorSnapshot, IReadOnlyDictionary<string, double>>? Sampled;
    public event Action<TestError>? ErrorReported;
    public event Action<RunLogEntry>? Logged;
    public event Action<StepResult>? StepCompleted;

    public RunResult Result { get; } = new();
    public bool IsRunning { get; private set; }

    public void Stop(string reason = "Stopped by user")
    {
        _userStopped = true;
        _abortReason ??= reason;
        _runCts.Cancel();
    }

    public async Task<RunResult> RunAsync()
    {
        IsRunning = true;
        var steps = _plan.Expand();
        double total = steps.Sum(s => s.DurationSeconds);
        var start = DateTime.Now;
        var result = Result;
        result.Name = _plan.Name;
        result.Plan = _plan;
        result.Hardware = _hardware;
        result.StartTime = start;
        result.AppVersion = typeof(PlanRunner).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

        SessionJournal? journal = null;
        if (_useJournal)
        {
            journal = SessionJournal.Start(new JournalHeader
            {
                RunId = result.Id, Name = result.Name, StartTime = start,
                BootTime = DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64),
                ProcessId = Environment.ProcessId, Plan = _plan, Hardware = _hardware,
            }, _journalRoot);
        }

        var recorder = new TimelineRecorder(start, total);
        int stepIndex = -1;
        double doneSeconds = 0;
        DateTime stepStart = start;
        TestStep? current = null;
        int overTemp = 0, overGpuTemp = 0, overHard = 0;
        double? lastAc = null;
        // Laptops are built to sit at their thermal limit: require the limit to be held longer before stopping.
        int hold = _plan.Settings.LaptopRules ? 10 : 3;

        // Sensor sampling + progress + heartbeat loop (1 Hz).
        var samplerCts = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            int tick = 0;
            while (!samplerCts.IsCancellationRequested)
            {
                var t0 = Stopwatch.GetTimestamp();
                try
                {
                    var snap = SafeRead();
                    foreach (var (id, m) in _metrics)
                        snap.Readings.Add(new SensorReading("test/" + id, "Test", HwType.Test, SensorKind.Other, m.Name, m.Value, m.Unit));
                    var key = KeyMetrics.From(snap);
                    snap.Readings.AddRange(KeyMetrics.AsReadings(key));
                    recorder.Add(snap, key);
                    Sampled?.Invoke(snap, key);

                    lock (_tail)
                    {
                        _tail.AddLast(new HeartbeatSample { Time = snap.Time, Metrics = key });
                        while (_tail.Count > 180) _tail.RemoveFirst();
                    }

                    // Safety limits (3 consecutive seconds over the limit).
                    if (current != null)
                    {
                        if (key.TryGetValue(KeyMetrics.CpuTemp, out var ct) && ct >= _plan.Settings.CpuTempLimit) overTemp++; else overTemp = 0;
                        var gt = Math.Max(key.GetValueOrDefault(KeyMetrics.GpuTemp), 0);
                        if (gt >= _plan.Settings.GpuTempLimit) overGpuTemp++; else overGpuTemp = 0;
                        if (key.TryGetValue(KeyMetrics.CpuTemp, out var hard) && hard >= 105) overHard++; else overHard = 0;
                        if (overTemp >= hold) Abort($"Safety stop: CPU reached {ct:0} °C (limit {_plan.Settings.CpuTempLimit:0} °C for {hold} s)");
                        if (overGpuTemp >= hold) Abort($"Safety stop: GPU reached {gt:0} °C (limit {_plan.Settings.GpuTempLimit:0} °C for {hold} s)");
                        if (overHard >= 3) Abort($"Safety stop: CPU reached {hard:0} °C — above any safe maximum");
                    }
                    if (key.TryGetValue(KeyMetrics.OnAc, out var ac))
                    {
                        if (lastAc == 1 && ac < 0.5) AddLog("Charger disconnected — the PC is now on battery and power-limited", Severity.Warning, journal);
                        if (lastAc == 0 && ac >= 0.5) AddLog("Charger reconnected", Severity.Info, journal);
                        lastAc = ac;
                    }

                    var cur = current;
                    if (cur != null)
                    {
                        var stepElapsed = (DateTime.Now - stepStart).TotalSeconds;
                        var overall = doneSeconds + Math.Min(stepElapsed, cur.DurationSeconds);
                        var remaining = Math.Max(0, total - overall);
                        ProgressChanged?.Invoke(new RunProgress
                        {
                            StepIndex = stepIndex, StepCount = steps.Count, StepName = cur.Name, StepKind = cur.Kind, Components = cur.Components,
                            StepFraction = Math.Clamp(stepElapsed / cur.DurationSeconds, 0, 1),
                            OverallFraction = total > 0 ? Math.Clamp(overall / total, 0, 1) : 0,
                            Elapsed = DateTime.Now - start,
                            Remaining = TimeSpan.FromSeconds(remaining),
                            StepRemaining = TimeSpan.FromSeconds(Math.Max(0, cur.DurationSeconds - stepElapsed)),
                            EstimatedEnd = DateTime.Now.AddSeconds(remaining),
                            Phases = new Dictionary<Component, (string, double)>(_phases),
                            ErrorCount = _errorCount,
                        });

                        if (journal != null && tick % 2 == 0)
                        {
                            List<HeartbeatSample> tailCopy;
                            lock (_tail) tailCopy = _tail.ToList();
                            List<TestError> recent;
                            lock (_errors) recent = _errors.TakeLast(20).ToList();
                            journal.Heartbeat(new HeartbeatState
                            {
                                Time = DateTime.Now, StepIndex = stepIndex, StepName = cur.Name, StepKind = cur.Kind, Components = cur.Components,
                                StepElapsedSeconds = stepElapsed, RunElapsedSeconds = (DateTime.Now - start).TotalSeconds,
                                ErrorCount = _errorCount, RecentErrors = recent, Tail = tailCopy,
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    DataPaths.AppLogWrite("Sampler error: " + ex);
                }
                tick++;
                var spent = Stopwatch.GetElapsedTime(t0);
                var wait = TimeSpan.FromSeconds(1) - spent;
                if (wait > TimeSpan.Zero)
                {
                    try { await Task.Delay(wait, samplerCts.Token); } catch (OperationCanceledException) { }
                }
            }
        });

        try
        {
            for (int i = 0; i < steps.Count && !_runCts.IsCancellationRequested; i++)
            {
                var step = steps[i];
                stepIndex = i;
                stepStart = DateTime.Now;
                _phases.Clear();
                _metrics.Clear();
                current = step;
                var sr = new StepResult { Index = i, Name = step.Name, Kind = step.Kind, Components = step.Components.ToList(), Variant = step.Variant, PlannedSeconds = step.DurationSeconds, Start = stepStart, Status = "Running" };
                result.Steps.Add(sr);
                AddLog($"Step {i + 1}/{steps.Count}: {step.Name} started", Severity.Info, journal);

                using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(_runCts.Token);
                // Hard deadline: modules should stop themselves, but never let a step overrun badly.
                stepCts.CancelAfter(TimeSpan.FromSeconds(step.DurationSeconds + 45));

                var tasks = step.Components.Distinct().Select(c =>
                {
                    var module = _factory.Create(c, step.Kind);
                    var ctx = new StepContext(step, _plan.Settings, stepCts.Token, stepStart, c,
                        (comp, phase, f) => _phases[comp] = (phase, f),
                        OnError,
                        (id, name, value, unit) => _metrics[id] = (name, value, unit),
                        (msg, sev) => AddLog(msg, sev, journal));
                    return Task.Run(async () =>
                    {
                        try { return await module.RunAsync(ctx); }
                        catch (OperationCanceledException) { return new ModuleResult { Component = c, Kind = step.Kind, Failed = true, FailureReason = "Cancelled" }; }
                        catch (Exception ex)
                        {
                            AddLog($"{c.Display()} test failed: {ex.Message}", Severity.Critical, journal);
                            return new ModuleResult { Component = c, Kind = step.Kind, Failed = true, FailureReason = ex.Message };
                        }
                    });
                }).ToList();

                var modules = await Task.WhenAll(tasks);
                sr.Modules = modules.ToList();
                sr.End = DateTime.Now;
                sr.Status = _runCts.IsCancellationRequested ? "Stopped" : modules.Any(m => m.Failed || m.ErrorCount > 0) ? "Errors" : "Passed";
                doneSeconds += step.DurationSeconds;
                AddLog($"Step {i + 1}/{steps.Count}: {step.Name} finished — {sr.Status}", sr.Status == "Passed" ? Severity.Info : Severity.Warning, journal);
                journal?.SaveCompletedSteps(result.Steps);
                StepCompleted?.Invoke(sr);
            }
        }
        finally
        {
            current = null;
            samplerCts.Cancel();
            try { await sampler; } catch { }
            IsRunning = false;
        }

        result.EndTime = DateTime.Now;
        recorder.Fill(result);
        lock (_errors) result.Errors = _errors.Take(500).ToList();
        result.TotalErrorCount = _errorCount;
        result.Log = _log.ToList();
        result.AbortReason = _abortReason;
        ComputeScores(result);

        bool anyFailed = result.AllModules.Any(m => m.Failed && m.FailureReason != "Cancelled");
        result.Status = _abortReason != null
            ? (_userStopped ? RunStatus.Cancelled : RunStatus.Aborted)
            : (_errorCount > 0 || anyFailed) ? RunStatus.CompletedWithErrors : RunStatus.Completed;

        RunAnalyzer.Analyze(result);
        journal?.Complete();
        return result;
    }

    private SensorSnapshot SafeRead()
    {
        try { return _sensors.Read(); }
        catch (Exception ex)
        {
            DataPaths.AppLogWrite("Sensor read failed: " + ex.Message);
            return new SensorSnapshot();
        }
    }

    private void Abort(string reason)
    {
        if (_abortReason != null) return;
        _abortReason = reason;
        AddLog(reason, Severity.Critical, null);
        _runCts.Cancel();
    }

    private void OnError(TestError e)
    {
        Interlocked.Increment(ref _errorCount);
        lock (_errors) { if (_errors.Count < 5000) _errors.Add(e); }
        ErrorReported?.Invoke(e);
        if (_plan.Settings.StopOnError) Abort($"Stopped on first error: {e.Component.Display()} {e.Test} — {e.Message}");
    }

    private void AddLog(string msg, Severity sev, SessionJournal? journal)
    {
        var entry = new RunLogEntry { Time = DateTime.Now, Severity = sev, Message = msg };
        lock (_log) _log.Add(entry);
        journal?.Log(msg);
        Logged?.Invoke(entry);
    }

    public static void ComputeScores(RunResult result)
    {
        result.ComponentScores.Clear();
        foreach (var g in result.AllModules.Where(m => m.Kind == StepKind.Benchmark && m.Score is > 0 && !m.Failed).GroupBy(m => m.Component))
            result.ComponentScores[g.Key] = Math.Round(g.Average(m => m.Score!.Value), 0);
        result.OverallScore = result.ComponentScores.Count > 0 ? Math.Round(Scoring.Score.GeoMean(result.ComponentScores.Values), 0) : null;
    }
}
