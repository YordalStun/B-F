using System.Collections.ObjectModel;
using System.Windows;
using BenchForge.App.Controls;
using BenchForge.App.Platform;
using BenchForge.Core.Engine;
using BenchForge.Core.Model;
using BenchForge.Core.Persistence;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BenchForge.App.ViewModels;

public sealed partial class StepRow : ObservableObject
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public string Parts { get; init; } = "";
    public string Duration { get; init; } = "";
    [ObservableProperty] private string _status = "Pending";
    [ObservableProperty] private string _glyph = "";
}

public sealed partial class LiveRunViewModel : PageViewModel
{
    private PlanRunner? _runner;
    private Views.OverlayWindow? _overlay;
    private DateTime _start;

    public LiveRunViewModel(MainViewModel main) : base(main, "Live test", "")
    {
        Charts = MetricChart.Standard();
        foreach (var id in new[] { KeyMetrics.CpuTemp, KeyMetrics.CpuLoad, KeyMetrics.CpuClock, KeyMetrics.CpuPower, KeyMetrics.GpuTemp, KeyMetrics.GpuLoad, KeyMetrics.GpuClock, KeyMetrics.GpuPower, KeyMetrics.RamLoad, KeyMetrics.GpuHotspot })
            Tiles.Add(new MetricTile(id, KeyMetrics.Label(id), KeyMetrics.Unit(id)));
        TestMetric = new ObservableCollection<ChartSeries>();
    }

    public MetricChart[] Charts { get; }
    public ObservableCollection<MetricTile> Tiles { get; }  = new();
    public ObservableCollection<ChartSeries> TestMetric { get; }
    public ObservableCollection<StepRow> Steps { get; } = new();
    public ObservableCollection<string> Phases { get; } = new();
    public ObservableCollection<TestError> Errors { get; } = new();
    public ObservableCollection<RunLogEntry> Log { get; } = new();
    public bool ShowOverlayOnStart { get; set; } = true;

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _planName = "";
    [ObservableProperty] private double _overall;
    [ObservableProperty] private string _overallText = "";
    [ObservableProperty] private string _remainingText = "";
    [ObservableProperty] private string _finishText = "";
    [ObservableProperty] private string _elapsedText = "";
    [ObservableProperty] private string _stepName = "";
    [ObservableProperty] private string _stepCounter = "";
    [ObservableProperty] private double _stepFraction;
    [ObservableProperty] private string _stepRemaining = "";
    [ObservableProperty] private int _errorCount;
    [ObservableProperty] private string _testMetricTitle = "Test throughput";
    [ObservableProperty] private bool _overlayVisible;
    [ObservableProperty] private string _overlayLine1 = "";
    [ObservableProperty] private string _overlayCpu = "";
    [ObservableProperty] private string _overlayGpu = "";
    [ObservableProperty] private string _overlayRam = "";
    [ObservableProperty] private string _overlayExtra = "";

    public void Start(TestPlan plan)
    {
        var svc = Main.S;
        if (svc.Sensors == null) { MessageBox.Show("Sensors are still starting — try again in a few seconds.", "BenchForge"); return; }
        PlanName = plan.Name;
        Steps.Clear(); Errors.Clear(); Log.Clear(); Phases.Clear(); TestMetric.Clear();
        foreach (var c in Charts) c.Clear();
        ErrorCount = 0; Overall = 0; StepFraction = 0;
        int i = 0;
        foreach (var st in plan.Expand())
            Steps.Add(new StepRow { Index = i++, Name = st.Name, Parts = string.Join(" + ", st.Components.Select(c => c.Display())), Duration = TimeFormat.Short(TimeSpan.FromSeconds(st.DurationSeconds)) });

        _runner = new PlanRunner(plan, new ModuleFactory(), svc.Sensors, svc.Inventory);
        _runner.ProgressChanged += p => Ui.Post(() => OnProgress(p));
        _runner.Sampled += (snap, key) => Ui.Post(() => OnSample(snap, key));
        _runner.ErrorReported += e => Ui.Post(() => { ErrorCount++; if (Errors.Count < 300) Errors.Insert(0, e); });
        _runner.Logged += l => Ui.Post(() => { Log.Insert(0, l); if (Log.Count > 500) Log.RemoveAt(Log.Count - 1); });
        _runner.StepCompleted += sr => Ui.Post(() =>
        {
            if (sr.Index < Steps.Count) { Steps[sr.Index].Status = sr.Status; Steps[sr.Index].Glyph = sr.Status == "Passed" ? "" : sr.Status == "Stopped" ? "" : ""; }
        });
        IsRunning = true;
        _start = DateTime.Now;
        RemainingText = TimeFormat.Long(TimeSpan.FromSeconds(plan.TotalSeconds));
        if (plan.Settings.ReopenAfterCrash) AutoReopen.Enable();
        if (ShowOverlayOnStart) ShowOverlay();

        var runner = _runner;
        Task.Run(async () =>
        {
            RunResult? result = null;
            try { result = await runner.RunAsync(); }
            catch (Exception ex) { DataPaths.AppLogWrite("Run failed: " + ex); }
            Ui.Post(() => Finish(result));
        });
    }

    private void Finish(RunResult? result)
    {
        IsRunning = false;
        AutoReopen.Disable();
        HideOverlay();
        if (result == null) { MessageBox.Show("The test run failed to complete. See the log in the data folder.", "BenchForge"); return; }
        try { Main.S.Store.Save(result); } catch (Exception ex) { DataPaths.AppLogWrite("Save failed: " + ex); }
        Main.History.Refresh();
        Main.ShowResult(result);
        try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
    }

    private void OnProgress(RunProgress p)
    {
        Overall = p.OverallFraction;
        OverallText = $"{p.OverallFraction:P0}";
        RemainingText = TimeFormat.Long(p.Remaining);
        FinishText = $"finishes ≈ {p.EstimatedEnd:HH:mm}";
        ElapsedText = $"{TimeFormat.Long(p.Elapsed)} elapsed";
        StepName = p.StepName;
        StepCounter = $"Step {p.StepIndex + 1} of {p.StepCount} · {(p.StepKind == StepKind.Benchmark ? "Benchmark" : "Stress")} · {string.Join(" + ", p.Components.Select(c => c.Display()))}";
        StepFraction = p.StepFraction;
        StepRemaining = TimeFormat.Short(p.StepRemaining) + " left in step";
        for (int i = 0; i < Steps.Count; i++)
        {
            if (i == p.StepIndex && Steps[i].Status == "Pending") { Steps[i].Status = "Running"; Steps[i].Glyph = ""; }
        }
        Phases.Clear();
        foreach (var (c, ph) in p.Phases) Phases.Add($"{c.Display()}: {ph.Phase}");
        OverlayLine1 = $"{p.StepName} · {RemainingText} left";
        OverlayExtra = (p.ErrorCount > 0 ? $"⚠ {p.ErrorCount} errors" : "No errors") + $" · {p.OverallFraction:P0}";
    }

    private void OnSample(SensorSnapshot snap, IReadOnlyDictionary<string, double> key)
    {
        double t = (snap.Time - _start).TotalSeconds;
        foreach (var c in Charts) c.Push(t, key, 0);
        foreach (var tile in Tiles) if (key.TryGetValue(tile.Id, out var v)) tile.Push(v);
        // Test's own live metrics (FPS, MB/s, points/s) — one chart line per metric.
        foreach (var r in snap.Readings.Where(r => r.HwType == HwType.Test))
        {
            var s = TestMetric.FirstOrDefault(x => x.Name == r.Name);
            if (s == null)
            {
                s = new ChartSeries(r.Name, Palette.Series[TestMetric.Count % Palette.Series.Length], r.Unit);
                TestMetric.Add(s);
            }
            s.Add(t, r.Value);
        }
        var fps = snap.Readings.FirstOrDefault(r => r.Id == "test/gpu.fps");
        string V(string k, string unit = "") => key.TryGetValue(k, out var v) ? MetricTile.Format(v, unit.Length > 0 ? unit : KeyMetrics.Unit(k)) : "–";
        OverlayCpu = $"CPU  {V(KeyMetrics.CpuTemp)}  {V(KeyMetrics.CpuLoad)}  {V(KeyMetrics.CpuClock)}  {V(KeyMetrics.CpuPower)}";
        OverlayGpu = $"GPU  {V(KeyMetrics.GpuTemp)}  {V(KeyMetrics.GpuLoad)}  {V(KeyMetrics.GpuClock)}  {V(KeyMetrics.GpuPower)}" + (fps != null ? $"  {fps.Value:0} FPS" : "");
        OverlayRam = $"RAM  {V(KeyMetrics.RamLoad)}" + (key.ContainsKey(KeyMetrics.GpuHotspot) ? $"   Hot spot {V(KeyMetrics.GpuHotspot)}" : "") + (key.ContainsKey(KeyMetrics.DiskTemp) ? $"   Drive {V(KeyMetrics.DiskTemp)}" : "");
    }

    /// <summary>Keeps the overlay alive between runs (shows idle sensors when no test runs).</summary>
    public void OnIdleSample(IReadOnlyDictionary<string, double> key)
    {
        if (IsRunning || !OverlayVisible) return;
        string V(string k) => key.TryGetValue(k, out var v) ? MetricTile.Format(v, KeyMetrics.Unit(k)) : "–";
        OverlayLine1 = "BenchForge · idle";
        OverlayCpu = $"CPU  {V(KeyMetrics.CpuTemp)}  {V(KeyMetrics.CpuLoad)}  {V(KeyMetrics.CpuClock)}";
        OverlayGpu = $"GPU  {V(KeyMetrics.GpuTemp)}  {V(KeyMetrics.GpuLoad)}  {V(KeyMetrics.GpuClock)}";
        OverlayRam = $"RAM  {V(KeyMetrics.RamLoad)}";
        OverlayExtra = "";
    }

    [RelayCommand]
    private void Stop()
    {
        if (_runner == null || !IsRunning) return;
        if (MessageBox.Show("Stop the test now? Results so far will be saved.", "BenchForge", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            _runner.Stop();
    }

    [RelayCommand]
    private void ToggleOverlay()
    {
        if (OverlayVisible) HideOverlay(); else ShowOverlay();
    }

    private void ShowOverlay()
    {
        _overlay ??= new Views.OverlayWindow { DataContext = this };
        _overlay.Show();
        OverlayVisible = true;
    }

    private void HideOverlay()
    {
        _overlay?.Hide();
        OverlayVisible = false;
    }
}
