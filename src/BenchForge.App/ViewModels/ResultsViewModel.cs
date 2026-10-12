using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using BenchForge.App.Controls;
using BenchForge.App.Platform;
using BenchForge.Core.Crash;
using BenchForge.Core.Model;
using BenchForge.Core.Persistence;
using BenchForge.Core.Reporting;
using BenchForge.Core.Scoring;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace BenchForge.App.ViewModels;

public sealed record ScoreCard(string Name, string Score, double Fraction, string Glyph);
public sealed record SubScoreRow(string Group, string Name, string Value, string Score, double Fraction, string? Detail);
public sealed record ModuleBlock(string Title, string Subtitle, IReadOnlyList<SubScoreRow> Rows, IReadOnlyList<KeyValuePair<string, string>> Info, string? Failure, IReadOnlyList<ModuleChart> Charts);
/// <summary>A chart a test produced itself (surface scan, sustained-write curve…).</summary>
public sealed record ModuleChart(string Title, string XUnit, string Unit, IReadOnlyList<ChartSeries> Series, string? Note)
{
    public static ModuleChart From(ChartData c)
    {
        var s = new ChartSeries(string.IsNullOrEmpty(c.YLabel) ? c.Unit : c.YLabel, Palette.Series[0], c.Unit);
        s.Set(c.Points.Where(p => p.Length >= 2).Select(p => new System.Windows.Point(p[0], p[1])));
        // "Position on disk (%)" → "%", "GB written" → "GB"
        int a = c.XLabel.IndexOf('('), b = c.XLabel.IndexOf(')');
        string unit = a >= 0 && b > a ? c.XLabel[(a + 1)..b] : c.XLabel.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return new ModuleChart(c.Title, unit, c.Unit, new[] { s }, c.Note);
    }
}
public sealed record StepBlock(string Title, string Status, IReadOnlyList<ModuleBlock> Modules);

public sealed partial class ChannelOption : ObservableObject
{
    public SensorChannel Channel { get; init; } = new();
    public string Group => Channel.Hardware;
    public string Name => Channel.Name + (Channel.Unit.Length > 0 ? $" ({Channel.Unit})" : "");
    [ObservableProperty] private bool _isChecked;
}

public sealed partial class ResultsViewModel : PageViewModel
{
    public ResultsViewModel(MainViewModel main) : base(main, "Results", "") { }

    [ObservableProperty] private RunResult? _result;
    [ObservableProperty] private string _header = "No result loaded";
    [ObservableProperty] private string _subHeader = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private RunStatus _status;
    [ObservableProperty] private string _overallScore = "";
    [ObservableProperty] private bool _hasCrash;
    [ObservableProperty] private string _crashTitle = "";
    [ObservableProperty] private string _hardwareText = "";
    [ObservableProperty] private string _chartUnitHint = "";
    [ObservableProperty] private string _channelFilter = "";

    public ObservableCollection<ScoreCard> Scores { get; } = new();
    public ObservableCollection<StepBlock> Details { get; } = new();
    public ObservableCollection<Recommendation> Recommendations { get; } = new();
    public ObservableCollection<Finding> Findings { get; } = new();
    public ObservableCollection<TestError> Errors { get; } = new();
    public ObservableCollection<RunLogEntry> Log { get; } = new();
    public ObservableCollection<ChannelStats> Stats { get; } = new();
    public ObservableCollection<string> CrashExplanation { get; } = new();
    public ObservableCollection<CrashSignal> CrashSignals { get; } = new();
    public ObservableCollection<MinidumpInfo> Dumps { get; } = new();
    public ObservableCollection<EventEntry> Events { get; } = new();
    public ObservableCollection<ChannelOption> Channels { get; } = new();
    public ObservableCollection<ChannelOption> FilteredChannels { get; } = new();
    public ObservableCollection<ChartSeries> ChartSeries { get; } = new();
    public MetricChart[] KeyCharts { get; private set; } = MetricChart.Standard();
    public ObservableCollection<ChartSeries> TestSeries { get; } = new();

    public void Load(RunResult r)
    {
        Result = r;
        Header = r.DisplayName;
        Status = r.Status;
        StatusText = HtmlReport.StatusText(r.Status) + (r.TotalErrorCount > 0 ? $" · {r.TotalErrorCount} errors" : "");
        SubHeader = $"{r.StartTime:dddd d MMM yyyy, HH:mm} · {TimeFormat.Short(r.Duration)} · {(r.Imported ? "imported from " + r.ImportedFrom : r.MachineName)}" + (r.AbortReason != null ? " · " + r.AbortReason : "");
        OverallScore = r.OverallScore is double overall ? Score.Format(overall) : "—";

        Scores.Clear();
        double maxScore = Math.Max(1000, r.ComponentScores.Values.DefaultIfEmpty(0).Max());
        foreach (var (comp, v) in r.ComponentScores)
            Scores.Add(new ScoreCard(comp.Display(), Score.Format(v), v / maxScore, comp switch { Component.Cpu => "", Component.Memory => "", Component.Storage => "", _ => "" }));

        Details.Clear();
        foreach (var s in r.Steps)
        {
            var mods = s.Modules.Select(m =>
            {
                double max = m.SubScores.Select(x => x.Score).DefaultIfEmpty(0).Max();
                var rows = m.SubScores.Select(x => new SubScoreRow(x.Group, x.Name, $"{x.Value:#,0.##} {x.Unit}".Trim(), x.Score > 0 ? Score.Format(x.Score) : "", max > 0 ? x.Score / max : 0, x.Detail)).ToList();
                return new ModuleBlock($"{m.Component.Display()}{(m.Score is double sc ? $" — {Score.Format(sc)} points" : "")}",
                    m.ErrorCount > 0 ? $"{m.ErrorCount} errors" : m.Failed ? "Failed" : "OK", rows, m.Info.ToList(), m.Failed ? m.FailureReason : null,
                    m.Charts.Select(ModuleChart.From).ToList());
            }).ToList();
            Details.Add(new StepBlock($"{s.Name} · {(s.Kind == StepKind.Benchmark ? "benchmark" : "stress")} · {TimeFormat.Short(TimeSpan.FromSeconds(s.PlannedSeconds))}", s.Status, mods));
        }

        Recommendations.Clear(); foreach (var x in r.Recommendations) Recommendations.Add(x);
        Findings.Clear(); foreach (var x in r.Findings) Findings.Add(x);
        Errors.Clear(); foreach (var x in r.Errors.Take(500)) Errors.Add(x);
        Log.Clear(); foreach (var x in r.Log) Log.Add(x);
        Stats.Clear();
        var stats = r.SensorStats.Count > 0 ? r.SensorStats.Where(x => x.HwType != HwType.Test && !x.Id.StartsWith("key/")) : r.KeyStats.Values;
        foreach (var x in stats.OrderBy(x => x.HwType).ThenBy(x => x.Hardware).ThenBy(x => x.Kind).ThenBy(x => x.Name)) Stats.Add(x);
        Events.Clear(); foreach (var e in r.RelatedEvents) Events.Add(e);

        HasCrash = r.Crash != null;
        CrashExplanation.Clear(); CrashSignals.Clear(); Dumps.Clear();
        if (r.Crash is CrashInfo crash)
        {
            CrashTitle = crash.Summary + (crash.SuspectedComponent is Component sc ? $" — most likely the {sc.Display()}" : "");
            foreach (var e in crash.Explanation) CrashExplanation.Add(e);
            foreach (var sig in crash.Signals) CrashSignals.Add(sig);
            foreach (var d in crash.Dumps) Dumps.Add(d);
        }

        HardwareText = BuildHardwareText(r.Hardware);

        // Key metric charts from the timeline.
        KeyCharts = MetricChart.Standard();
        OnPropertyChanged(nameof(KeyCharts));
        var keyIdx = r.Timeline.Channels.Select((c2, i) => (c2, i)).Where(x => x.c2.Id.StartsWith("key/")).ToList();
        foreach (var smp in r.Timeline.Samples)
        {
            var dict = new Dictionary<string, double>();
            foreach (var (ch, i) in keyIdx) if (i < smp.V.Length && smp.V[i] is float f) dict[ch.Id[4..]] = f;
            foreach (var kc in KeyCharts) kc.Push(smp.T, dict, 0);
        }
        TestSeries.Clear();
        foreach (var ch in r.Timeline.Channels.Where(c2 => c2.HwType == HwType.Test))
        {
            var s = new ChartSeries(ch.Name, Palette.Series[TestSeries.Count % Palette.Series.Length], ch.Unit);
            s.Set(r.Timeline.Series(ch.Id).Select(p => new Point(p.T, p.V)));
            TestSeries.Add(s);
        }

        Channels.Clear();
        foreach (var ch in r.Timeline.Channels.Where(c2 => !c2.Id.StartsWith("key/") && c2.HwType != HwType.Test).OrderBy(c2 => c2.HwType).ThenBy(c2 => c2.Hardware).ThenBy(c2 => c2.Kind))
        {
            var opt = new ChannelOption { Channel = ch };
            opt.PropertyChanged += (_, _) => RebuildChart();
            Channels.Add(opt);
        }
        // Pre-select CPU/GPU temperatures.
        foreach (var o in Channels.Where(o => o.Channel.Kind == SensorKind.Temperature && (o.Channel.HwType is HwType.Cpu or HwType.Gpu)).Take(6)) o.IsChecked = true;
        ApplyFilter();
        RebuildChart();
    }

    private static string BuildHardwareText(HardwareInventory? hw)
    {
        if (hw == null) return "No hardware information was recorded.";
        var lines = new List<string>
        {
            $"CPU: {hw.Cpu.Name} — {hw.Cpu.Cores} cores / {hw.Cpu.Threads} threads, max {hw.Cpu.MaxClockMHz} MHz, L2 {hw.Cpu.L2KB} KB, L3 {hw.Cpu.L3KB} KB, {hw.Cpu.InstructionSets}",
        };
        lines.AddRange(hw.Gpus.Select(g => $"GPU: {g.Name} — {g.VramGB:0.#} GB, driver {g.DriverVersion} ({g.DriverDate:yyyy-MM-dd}), {g.Resolution} @ {g.RefreshRate} Hz"));
        lines.Add($"RAM: {hw.TotalMemoryGB:0.#} GB");
        lines.AddRange(hw.MemoryModules.Select(m => $"   {m.Slot}: {m.CapacityGB:0} GB {m.Type} {m.Manufacturer} {m.PartNumber} — running {m.ConfiguredSpeedMTs} MT/s (rated {m.SpeedMTs})"));
        lines.AddRange(hw.Disks.Select(d => $"Drive: {d.Model} — {d.SizeGB:0} GB {d.BusType} {d.MediaType}, health {d.Health}" +
            $"{(d.TemperatureC != null ? $", {d.TemperatureC:0} °C" : "")}{(d.WearPercent != null ? $", wear {d.WearPercent:0}%" : "")}{(d.PowerOnHours != null ? $", {d.PowerOnHours} power-on hours" : "")}{(d.Volumes.Count > 0 ? ", " + string.Join(" ", d.Volumes) : "")}{(d.FreePercent != null ? $" ({d.FreePercent:0}% free)" : "")}"));
        lines.Add($"Motherboard: {hw.Motherboard}, BIOS {hw.BiosVersion} ({hw.BiosDate:yyyy-MM-dd})");
        lines.Add($"Windows: {hw.OsName} {hw.OsVersion}, last boot {hw.LastBoot:yyyy-MM-dd HH:mm}, power plan {hw.PowerPlan}");
        lines.AddRange(hw.Notes.Select(n => "Note: " + n));
        return string.Join(Environment.NewLine, lines);
    }

    partial void OnChannelFilterChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        FilteredChannels.Clear();
        foreach (var c in Channels.Where(c => string.IsNullOrWhiteSpace(ChannelFilter) || c.Group.Contains(ChannelFilter, StringComparison.OrdinalIgnoreCase) || c.Name.Contains(ChannelFilter, StringComparison.OrdinalIgnoreCase)))
            FilteredChannels.Add(c);
    }

    private void RebuildChart()
    {
        if (Result == null) return;
        ChartSeries.Clear();
        int i = 0;
        foreach (var o in Channels.Where(o => o.IsChecked).Take(12))
        {
            var s = new ChartSeries($"{o.Channel.Hardware} · {o.Channel.Name}", Palette.Series[i++ % Palette.Series.Length], o.Channel.Unit);
            s.Set(Result.Timeline.Series(o.Channel.Id).Select(p => new Point(p.T, p.V)));
            ChartSeries.Add(s);
        }
        var units = Channels.Where(o => o.IsChecked).Select(o => o.Channel.Unit).Distinct().ToList();
        ChartUnitHint = units.Count > 1 ? "Tip: pick sensors with the same unit for a readable scale." : "";
    }

    [RelayCommand]
    private void QuickSelect(string kind)
    {
        foreach (var o in Channels) o.IsChecked = false;
        var k = Enum.TryParse<SensorKind>(kind, out var sk) ? sk : SensorKind.Temperature;
        foreach (var o in Channels.Where(o => o.Channel.Kind == k).Take(10)) o.IsChecked = true;
    }

    [RelayCommand]
    private void ExportHtml()
    {
        if (Result == null) return;
        var dlg = new SaveFileDialog { Filter = "Web page (*.html)|*.html", FileName = $"BenchForge {Result.StartTime:yyyy-MM-dd HHmm} {Result.DisplayName}.html", InitialDirectory = DataPaths.Reports };
        if (dlg.ShowDialog() != true) return;
        File.WriteAllText(dlg.FileName, HtmlReport.Build(Result));
        try { Process.Start(new ProcessStartInfo(dlg.FileName) { UseShellExecute = true }); } catch { }
    }

    [RelayCommand]
    private void ExportResult()
    {
        if (Result == null) return;
        var dlg = new SaveFileDialog { Filter = "BenchForge result (*.bfresult)|*.bfresult", FileName = $"{Environment.MachineName} {Result.StartTime:yyyy-MM-dd HHmm} {Result.DisplayName}.bfresult" };
        if (dlg.ShowDialog() == true) Main.S.Store.Export(Result, dlg.FileName);
    }

    [RelayCommand]
    private void ExportForAi() { if (Result != null) AiExportService.Export(new[] { Result }); }

    [RelayCommand]
    private void AskAi()
    {
        if (Result != null) Main.AskAi($"Ask AI about \"{Result.DisplayName}\"", new[] { Result });
    }

    [RelayCommand]
    private async Task DeepAnalyze(MinidumpInfo? dump)
    {
        if (dump == null) return;
        if (DumpAnalyzer.FindCdb() == null)
        {
            MessageBox.Show("Deep dump analysis uses Microsoft's free debugger (cdb.exe).\n\nInstall 'Debugging Tools for Windows' from the Windows SDK installer, then try again.", "BenchForge");
            return;
        }
        try
        {
            Mouse.SetBusy(true);
            await DumpAnalyzer.AnalyzeAsync(dump, CancellationToken.None);
            if (Result?.Crash != null) { CrashAnalyzer.Explain(Result.Crash, Result.Plan.Settings); Load(Result); }
            MessageBox.Show(dump.DeepAnalysis ?? "No details found.", "Dump analysis");
        }
        catch (Exception ex) { MessageBox.Show("Analysis failed: " + ex.Message, "BenchForge"); }
        finally { Mouse.SetBusy(false); }
    }
}

internal static class Mouse
{
    public static void SetBusy(bool busy) => System.Windows.Input.Mouse.OverrideCursor = busy ? System.Windows.Input.Cursors.Wait : null;
}
