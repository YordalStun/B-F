using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using BenchForge.Core.Analysis;
using BenchForge.Core.Model;
using BenchForge.Core.Persistence;
using BenchForge.Core.Reporting;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace BenchForge.App.ViewModels;

public sealed partial class HistoryViewModel : PageViewModel
{
    public HistoryViewModel(MainViewModel main) : base(main, "History", "") { }

    public ObservableCollection<RunSummary> Runs { get; } = new();
    /// <summary>Set by the view from the grid's multi-selection.</summary>
    public List<RunSummary> Selection { get; set; } = new();
    [ObservableProperty] private string _summary = "";

    public void Refresh()
    {
        var list = Main.S.Store.List();
        Runs.Clear();
        foreach (var r in list) Runs.Add(r);
        int crashes = list.Count(r => r.Status == RunStatus.Crashed);
        Summary = $"{list.Count} runs saved · {crashes} crashed · stored in {Main.S.Store.Folder}";
    }

    public override void OnShown() => Refresh();

    private List<RunResult> LoadSelected() => Selection.Select(s => ResultStore.Load(s.FilePath)).Where(r => r != null).Select(r => r!).ToList();

    [RelayCommand]
    private void Open()
    {
        var r = LoadSelected().FirstOrDefault();
        if (r != null) Main.ShowResult(r);
    }

    [RelayCommand]
    private void CompareSelected()
    {
        var runs = LoadSelected();
        if (runs.Count < 2) { MessageBox.Show("Select two or more runs (hold Ctrl or Shift) to compare them.", "BenchForge"); return; }
        Main.ShowCompare(runs.OrderBy(r => r.StartTime).ToList());
    }

    [RelayCommand]
    private void CompareCrashes()
    {
        var crashed = Runs.Where(r => r.Status is RunStatus.Crashed or RunStatus.CompletedWithErrors).Take(2).ToList();
        if (crashed.Count < 2) { MessageBox.Show("There are fewer than two runs that crashed or found errors.", "BenchForge"); return; }
        Main.ShowCompare(crashed.Select(s => ResultStore.Load(s.FilePath)!).Where(r => r != null).OrderBy(r => r.StartTime).ToList());
    }

    [RelayCommand]
    private void AskAi()
    {
        var runs = LoadSelected();
        if (runs.Count == 0) { MessageBox.Show("Select one or more runs first.", "BenchForge"); return; }
        Main.AskAi($"Ask AI about {runs.Count} run(s)", runs);
    }

    [RelayCommand]
    private void ExportForAi()
    {
        var runs = LoadSelected();
        if (runs.Count == 0) runs = Runs.Take(10).Select(s => ResultStore.Load(s.FilePath)).Where(r => r != null).Select(r => r!).ToList();
        if (runs.Count == 0) { MessageBox.Show("There are no saved runs yet.", "BenchForge"); return; }
        AiExportService.Export(runs);
    }

    [RelayCommand]
    private void Import()
    {
        var dlg = new OpenFileDialog { Filter = "BenchForge results (*.bfresult)|*.bfresult|All files|*.*", Multiselect = true, Title = "Import results from another PC" };
        if (dlg.ShowDialog() != true) return;
        int ok = 0;
        foreach (var f in dlg.FileNames)
        {
            try { Main.S.Store.Import(f); ok++; }
            catch (Exception ex) { MessageBox.Show($"Could not import {Path.GetFileName(f)}: {ex.Message}", "BenchForge"); }
        }
        Refresh();
        if (ok > 0) MessageBox.Show($"Imported {ok} result(s). Select them together with your own runs and press Compare.", "BenchForge");
    }

    [RelayCommand]
    private void Export()
    {
        var runs = Selection.ToList();
        if (runs.Count == 0) { MessageBox.Show("Select the runs to export.", "BenchForge"); return; }
        var dlg = new OpenFolderDialog { Title = "Choose a folder (e.g. your USB stick) for the exported results" };
        if (dlg.ShowDialog() != true) return;
        foreach (var s in runs) File.Copy(s.FilePath, Path.Combine(dlg.FolderName, $"{s.MachineName} {Path.GetFileName(s.FilePath)}"), true);
        MessageBox.Show($"Exported {runs.Count} result file(s). On the other PC, open BenchForge › History › Import.", "BenchForge");
    }

    [RelayCommand]
    private void Delete()
    {
        var runs = Selection.ToList();
        if (runs.Count == 0) return;
        if (MessageBox.Show($"Delete {runs.Count} saved run(s)? This cannot be undone.", "BenchForge", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        foreach (var s in runs) { try { Main.S.Store.Delete(s.FilePath); } catch { } }
        Refresh();
    }

    [RelayCommand]
    private void OpenFolder() { try { Process.Start("explorer.exe", Main.S.Store.Folder); } catch { } }

    [RelayCommand] private void RefreshList() => Refresh();
}

public sealed record CompareCell(string Text, string? Delta, bool Better);
public sealed record CompareRow(string Group, string Name, IReadOnlyList<CompareCell> Cells);

public sealed partial class CompareViewModel : PageViewModel
{
    public CompareViewModel(MainViewModel main) : base(main, "Compare", "") { }

    public ObservableCollection<RunResult> Runs { get; } = new();
    public ObservableCollection<CompareRow> Rows { get; } = new();
    public ObservableCollection<Finding> Findings { get; } = new();
    public ObservableCollection<string> NextSteps { get; } = new();
    [ObservableProperty] private string _verdict = "";
    [ObservableProperty] private string _caseText = "";
    [ObservableProperty] private bool _hasPattern;
    private PatternReport? _report;

    /// <summary>Raised so the view can rebuild its dynamic columns.</summary>
    public event Action? Loaded;

    public void Load(IReadOnlyList<RunResult> runs)
    {
        Runs.Clear(); foreach (var r in runs) Runs.Add(r);
        Rows.Clear();
        foreach (var row in RunComparison.Build(runs))
        {
            double? first = row.Values.FirstOrDefault(v => v != null);
            var cells = row.Values.Select((v, i) =>
            {
                if (v is not double x) return new CompareCell("—", null, false);
                string? delta = null; bool better = false;
                if (i > 0 && first is double f0 && f0 != 0)
                {
                    double pct = (x / f0 - 1) * 100;
                    if (Math.Abs(pct) >= 1) { delta = $"{(pct > 0 ? "+" : "")}{pct:0}%"; better = row.HigherIsBetter ? pct > 0 : pct < 0; }
                }
                return new CompareCell($"{x:#,0.#} {row.Unit}".Trim(), delta, better);
            }).ToList();
            Rows.Add(new CompareRow(row.Group, row.Name, cells));
        }

        var failures = runs.Where(r => r.IsFailure).Select(FailureCase.FromRun).ToList();
        _report = PatternAnalyzer.Analyze(failures, runs.Where(r => !r.IsFailure).ToList());
        HasPattern = failures.Count > 0;
        CaseText = failures.Count == 0 ? "None of these runs crashed or found errors — comparing performance only."
                 : $"{failures.Count} of the {runs.Count} selected runs crashed or found errors. Looking for what they have in common:";
        Verdict = _report.Verdict;
        Findings.Clear(); foreach (var f in _report.Findings) Findings.Add(f);
        NextSteps.Clear(); foreach (var s in _report.NextSteps) NextSteps.Add(s);
        Loaded?.Invoke();
    }

    [RelayCommand]
    private void ExportForAi() => AiExportService.Export(Runs.ToList(), question: "Compare these runs. If any crashed or found errors, what do they have in common and what is the most likely cause? If none failed, explain the performance differences.");

    [RelayCommand]
    private void AskAi() => Main.AskAi($"Ask AI to compare {Runs.Count} runs", Runs.ToList(), question: "Compare these runs. If any crashed or found errors, what do they have in common and what is the most likely cause? If none failed, explain the performance differences.");

    [RelayCommand]
    private void ExportHtml()
    {
        var dlg = new SaveFileDialog { Filter = "Web page (*.html)|*.html", FileName = $"BenchForge comparison {DateTime.Now:yyyy-MM-dd HHmm}.html", InitialDirectory = DataPaths.Reports };
        if (dlg.ShowDialog() != true) return;
        File.WriteAllText(dlg.FileName, HtmlReport.BuildComparison(Runs.ToList(), _report));
        try { Process.Start(new ProcessStartInfo(dlg.FileName) { UseShellExecute = true }); } catch { }
    }

    [RelayCommand]
    private void OpenRun(RunResult? r) { if (r != null) Main.ShowResult(r); }
}
