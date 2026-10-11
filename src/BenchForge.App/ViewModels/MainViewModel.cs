using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using BenchForge.App.Platform;
using BenchForge.Core.Crash;
using BenchForge.Core.Model;
using BenchForge.Core.Persistence;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BenchForge.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    public Services S => Services.Instance;

    public MainViewModel()
    {
        Dashboard = new DashboardViewModel(this);
        Run = new RunSetupViewModel(this);
        Live = new LiveRunViewModel(this) { ShowInNav = false };
        Results = new ResultsViewModel(this) { ShowInNav = false };
        History = new HistoryViewModel(this);
        Compare = new CompareViewModel(this) { ShowInNav = false };
        Crashes = new CrashesViewModel(this);
        Events = new EventsViewModel(this);
        Settings = new SettingsViewModel(this);
        Pages = new ObservableCollection<PageViewModel> { Dashboard, Run, Live, Results, History, Compare, Crashes, Events, Settings };
        _currentPage = Dashboard;
        Dashboard.IsSelected = true;
    }

    public DashboardViewModel Dashboard { get; }
    public RunSetupViewModel Run { get; }
    public LiveRunViewModel Live { get; }
    public ResultsViewModel Results { get; }
    public HistoryViewModel History { get; }
    public CompareViewModel Compare { get; }
    public CrashesViewModel Crashes { get; }
    public EventsViewModel Events { get; }
    public SettingsViewModel Settings { get; }
    public ObservableCollection<PageViewModel> Pages { get; }

    [ObservableProperty] private PageViewModel _currentPage;
    [ObservableProperty] private string? _banner;
    [ObservableProperty] private bool _bannerIsCrash;
    [ObservableProperty] private string _statusText = "Starting sensors…";
    [ObservableProperty] private string _adminText = "";
    /// <summary>Icon-only sidebar for small / laptop screens.</summary>
    [ObservableProperty] private bool _isCompact;
    private bool? _sidebarOverride;

    public void UpdateLayoutMode(double windowWidth) => IsCompact = _sidebarOverride ?? windowWidth < 1150;

    public void ToggleSidebar(double windowWidth)
    {
        _sidebarOverride = !IsCompact;
        UpdateLayoutMode(windowWidth);
    }

    public string DataFolder => DataPaths.Root + (DataPaths.IsPortable ? "  (portable)" : "");

    public void Navigate(PageViewModel page)
    {
        CurrentPage = page;
        page.ShowInNav = page.ShowInNav || page == Live || page == Results || page == Compare;
        foreach (var p in Pages) if (p != page && p.IsSelected) p.IsSelected = false;
        if (!page.IsSelected) page.IsSelected = true;
        page.OnShown();
    }

    public void Initialize(string[] args)
    {
        S.SensorsReady += () => Ui.Post(() =>
        {
            StatusText = S.Sensors == null ? "Sensors unavailable" : "Sensors running";
            if (S.Sensors != null)
            {
                S.Sensors.Updated += (snap, key) => Ui.Post(() => { Dashboard.OnSample(snap, key); Live.OnIdleSample(key); });
                if (S.Sensors.Warning != null) Dashboard.SensorWarning = S.Sensors.Warning;
            }
        });
        S.InventoryReady += () => Ui.Post(() =>
        {
            Dashboard.SetInventory(S.Inventory!);
            Dashboard.LoadSystemChecks(S.Inventory!);
            Run.OnInventory(S.Inventory!);
            AdminText = S.Inventory!.IsAdmin ? "Administrator" : "Not administrator — some sensors/logs unavailable";
        });
        S.Start();
        History.Refresh();
        Task.Run(() => RecoverCrashes(args.Contains("--after-crash")));
    }

    /// <summary>Looks for test sessions that never finished: that means the PC (or BenchForge) went down mid-test.</summary>
    private void RecoverCrashes(bool launchedAfterCrash)
    {
        try
        {
            var unfinished = SessionJournal.FindUnfinished();
            RunResult? latest = null;
            foreach (var rec in unfinished)
            {
                // Another BenchForge instance that is still running owns this journal.
                try
                {
                    var p = Process.GetProcessById(rec.Header.ProcessId);
                    if (p.Id != Environment.ProcessId && p.ProcessName.StartsWith("BenchForge", StringComparison.OrdinalIgnoreCase)) continue;
                }
                catch { }
                var result = CrashAnalyzer.FromJournal(rec, S.Events);
                S.Store.Save(result);
                SessionJournal.Discard(rec);
                if (latest == null || result.StartTime > latest.StartTime) latest = result;
            }
            AutoReopen.Disable();
            if (latest != null)
            {
                var r = latest;
                Ui.Post(() =>
                {
                    History.Refresh();
                    Banner = $"The last test did not finish: {r.Crash?.Summary}. Here is what BenchForge found.";
                    BannerIsCrash = true;
                    ShowResult(r);
                });
            }
        }
        catch (Exception ex) { DataPaths.AppLogWrite("Crash recovery failed: " + ex); }
    }

    public void StartRun(TestPlan plan)
    {
        if (Live.IsRunning) { MessageBox.Show("A test is already running.", "BenchForge"); return; }
        Live.ShowInNav = true;
        Navigate(Live);
        Live.Start(plan);
    }

    public void ShowResult(RunResult r)
    {
        Results.Load(r);
        Results.ShowInNav = true;
        Navigate(Results);
    }

    public void ShowCompare(IReadOnlyList<RunResult> runs)
    {
        Compare.Load(runs);
        Compare.ShowInNav = true;
        Navigate(Compare);
    }

    public void AskAi(string title, IReadOnlyList<RunResult> runs, IReadOnlyList<SystemCrash>? crashes = null, IReadOnlyList<EventEntry>? events = null, string? question = null)
    {
        var w = new Views.AiWindow(new AiChatViewModel(this, title, runs, crashes, events, question)) { Owner = Application.Current.MainWindow };
        w.Show();
    }

    [RelayCommand]
    private void RunFix(Core.Analysis.FixInfo? fix)
    {
        if (fix == null) return;
        var msg = $"{fix.Title}\n\n{fix.WhatItDoes}\n\n{(fix.NeedsRestart ? "A restart is needed afterwards.\n\n" : "")}To undo: {fix.HowToUndo}\n\nGo ahead?";
        if (MessageBox.Show(msg, "BenchForge fix", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        new Views.FixWindow(fix) { Owner = Application.Current.MainWindow }.Show();
    }

    [RelayCommand] private Task ExportEverythingForAi() => AiExportService.ExportEverything();

    [RelayCommand] private void DismissBanner() => Banner = null;
    [RelayCommand] private void OpenDataFolder() { try { Process.Start("explorer.exe", DataPaths.Root); } catch { } }
}
