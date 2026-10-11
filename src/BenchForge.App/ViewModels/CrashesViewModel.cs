using System.Collections.ObjectModel;
using System.Windows;
using BenchForge.App.Platform;
using BenchForge.Core.Analysis;
using BenchForge.Core.Crash;
using BenchForge.Core.Events;
using BenchForge.Core.Model;
using BenchForge.Core.Persistence;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BenchForge.App.ViewModels;

public sealed partial class CrashItem : ObservableObject
{
    public DateTime Time { get; init; }
    public string Title { get; init; } = "";
    public string Source { get; init; } = "";
    public string Suspect { get; init; } = "";
    public SystemCrash Crash { get; init; } = new();
    public string? RunPath { get; init; }
    public RunResult? Run { get; set; }
}

public sealed partial class CrashesViewModel : PageViewModel
{
    public CrashesViewModel(MainViewModel main) : base(main, "Crashes", "") { }

    public ObservableCollection<CrashItem> Crashes { get; } = new();
    public List<CrashItem> Selection { get; set; } = new();
    public int[] DayOptions { get; } = { 7, 30, 90, 365, 3650 };
    [ObservableProperty] private int _days = 90;
    [ObservableProperty] private CrashItem? _selected;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _summary = "";
    // Detail
    public ObservableCollection<string> Explanation { get; } = new();
    public ObservableCollection<CrashSignal> Signals { get; } = new();
    public ObservableCollection<MinidumpInfo> Dumps { get; } = new();
    public ObservableCollection<string> Fixes { get; } = new();
    [ObservableProperty] private string _detailTitle = "";
    // Pattern
    public ObservableCollection<Finding> PatternFindings { get; } = new();
    public ObservableCollection<string> PatternSteps { get; } = new();
    [ObservableProperty] private string? _patternVerdict;
    private bool _loaded;

    public override void OnShown() { if (!_loaded) _ = RefreshAsync(); }

    partial void OnDaysChanged(int value) => _ = RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        _loaded = true;
        IsBusy = true;
        Summary = "Reading Windows event logs and crash dumps…";
        var since = DateTime.Now.AddDays(-Days);
        var items = await Task.Run(() =>
        {
            var list = new List<CrashItem>();
            try
            {
                var spec = new EventQuerySpec { Since = since, Description = "crashes" };
                foreach (var p in new[] { EventPresets.BlueScreens, EventPresets.UnexpectedShutdowns, EventPresets.Whea, EventPresets.GpuDriver, EventPresets.Disk, EventPresets.Thermal })
                    spec.Selectors.AddRange(p.Selectors);
                var events = Main.S.Events.Query(spec, 2000);
                var dumps = MinidumpReader.ListDumps(null, since);
                foreach (var c in CrashAnalyzer.FindSystemCrashes(events, dumps))
                    list.Add(new CrashItem { Time = c.Time, Title = c.Info.Summary, Source = "Windows", Suspect = c.Info.SuspectedComponent?.Display() ?? "", Crash = c });
            }
            catch (Exception ex) { DataPaths.AppLogWrite("Crash scan failed: " + ex); }
            // Crashes recorded by BenchForge test runs (with sensor data).
            foreach (var s in Main.S.Store.List().Where(s => s.Status == RunStatus.Crashed && s.StartTime >= since))
            {
                var r = ResultStore.Load(s.FilePath);
                if (r?.Crash == null) continue;
                // Merge with the matching Windows crash if there is one.
                var match = list.FirstOrDefault(x => x.Source == "Windows" && Math.Abs((x.Time - r.Crash.LastHeartbeat).TotalMinutes) < 30 && x.Run == null);
                if (match != null) list.Remove(match);
                list.Add(new CrashItem
                {
                    Time = r.Crash.LastHeartbeat, Title = r.Crash.Summary, Source = $"During test: {r.Crash.StepName ?? r.DisplayName}",
                    Suspect = r.Crash.SuspectedComponent?.Display() ?? "", Crash = new SystemCrash { Time = r.Crash.LastHeartbeat, Info = r.Crash, RunId = r.Id }, RunPath = s.FilePath, Run = r,
                });
            }
            return list.OrderByDescending(x => x.Time).ToList();
        });
        Crashes.Clear();
        foreach (var i in items) Crashes.Add(i);
        Summary = items.Count == 0 ? $"No crashes found in the last {Days} days. 🎉" : $"{items.Count} crashes in the last {Days} days · {items.Count(i => i.Run != null)} happened during BenchForge tests";
        Selected = Crashes.FirstOrDefault();
        IsBusy = false;
    }

    partial void OnSelectedChanged(CrashItem? value)
    {
        Explanation.Clear(); Signals.Clear(); Dumps.Clear(); Fixes.Clear();
        if (value == null) { DetailTitle = ""; return; }
        var info = value.Crash.Info;
        DetailTitle = $"{value.Time:dddd d MMM yyyy HH:mm} — {info.Summary}";
        foreach (var e in info.Explanation) Explanation.Add(e);
        foreach (var s in info.Signals) Signals.Add(s);
        foreach (var d in info.Dumps) Dumps.Add(d);
        uint? code = info.Signals.FirstOrDefault(s => s.BugCheckCode is > 0)?.BugCheckCode ?? info.Dumps.FirstOrDefault(d => d.Valid)?.BugCheckCode;
        if (code is uint c && BugCheckKnowledgeBase.Get(c) is BugCheckInfo kb) foreach (var f in kb.Fixes) Fixes.Add(f);
        else if (info.Kind == CrashKind.PowerLossOrHang)
            foreach (var f in new[] { "Check the power supply (age, wattage, cables) — sudden shut-offs under load are often the PSU", "Check temperatures with the Burn test", "Undo overclocks/undervolts", "Update the BIOS" }) Fixes.Add(f);
    }

    private void ShowPattern(IReadOnlyList<CrashItem> items)
    {
        var cases = items.Select(i => i.Run != null ? FailureCase.FromRun(i.Run) : FailureCase.FromSystemCrash(i.Crash)).ToList();
        var rep = PatternAnalyzer.Analyze(cases);
        PatternVerdict = $"Compared {items.Count} crashes: {rep.Verdict}";
        PatternFindings.Clear(); foreach (var f in rep.Findings) PatternFindings.Add(f);
        PatternSteps.Clear(); foreach (var s in rep.NextSteps) PatternSteps.Add(s);
    }

    [RelayCommand]
    private void CompareLastTwo()
    {
        if (Crashes.Count < 2) { MessageBox.Show("There need to be at least two crashes to compare.", "BenchForge"); return; }
        ShowPattern(Crashes.Take(2).ToList());
    }

    [RelayCommand]
    private void CompareSelected()
    {
        if (Selection.Count < 2) { MessageBox.Show("Select two or more crashes (Ctrl/Shift-click).", "BenchForge"); return; }
        ShowPattern(Selection.ToList());
    }

    [RelayCommand]
    private void OpenRun()
    {
        if (Selected?.Run != null) Main.ShowResult(Selected.Run);
    }

    [RelayCommand]
    private void AskAi()
    {
        var items = Selection.Count > 0 ? Selection.ToList() : Crashes.Take(5).ToList();
        if (items.Count == 0) return;
        Main.AskAi($"Ask AI about {items.Count} crash(es)", items.Where(i => i.Run != null).Select(i => i.Run!).ToList(), items.Where(i => i.Run == null).Select(i => i.Crash).ToList(),
            question: "Look at these crashes. What do they have in common, what is the most likely cause, and what should I do?");
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
        IsBusy = true;
        try
        {
            await DumpAnalyzer.AnalyzeAsync(dump, CancellationToken.None);
            if (Selected != null) { CrashAnalyzer.Explain(Selected.Crash.Info, null); OnSelectedChanged(Selected); }
            MessageBox.Show(dump.DeepAnalysis ?? "No details found.", "Dump analysis");
        }
        catch (Exception ex) { MessageBox.Show("Analysis failed: " + ex.Message, "BenchForge"); }
        finally { IsBusy = false; }
    }
}
