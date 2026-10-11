using System.Collections.ObjectModel;
using System.Windows;
using BenchForge.App.Platform;
using BenchForge.Core.Ai;
using BenchForge.Core.Model;
using BenchForge.Core.Persistence;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BenchForge.App.ViewModels;

public sealed record ProviderOption(AiProviderKind Kind, string Title, string Description);

public sealed partial class LocalModelOption : ObservableObject
{
    public LocalModelInfo Info { get; init; } = null!;
    [ObservableProperty] private bool _isDownloaded;
    public string Display => Info.Display;
    public string Description => Info.Description;
    public string State => IsDownloaded ? "Downloaded" : "Not downloaded";
    partial void OnIsDownloadedChanged(bool value) => OnPropertyChanged(nameof(State));
}

public sealed partial class SettingsViewModel : PageViewModel
{
    private CancellationTokenSource? _downloadCts;
    private bool _init = true;

    public SettingsViewModel(MainViewModel main) : base(main, "Settings", "")
    {
        var s = Main.S.Settings;
        Providers = new[]
        {
            new ProviderOption(AiProviderKind.None, "Off", "Only the built-in analysis (works everywhere, no internet)."),
            new ProviderOption(AiProviderKind.LocalModel, "Local AI model (offline)", "Downloads a model to the BenchForge folder and runs it on this PC. Nothing leaves the PC."),
            new ProviderOption(AiProviderKind.Claude, "Claude (online)", "Most capable. Needs internet and your Anthropic API key."),
            new ProviderOption(AiProviderKind.CustomServer, "Ollama / LM Studio server", "Use an AI server you already run (OpenAI-compatible)."),
        };
        _provider = Providers.First(p => p.Kind == s.Ai.Provider);
        foreach (var m in LocalModelCatalog.Models) LocalModels.Add(new LocalModelOption { Info = m, IsDownloaded = LocalAiManager.IsModelDownloaded(m) });
        _selectedModel = LocalModels.FirstOrDefault(m => m.Info.Id == s.Ai.LocalModelId) ?? LocalModels[2];
        _claudeModel = s.Ai.ClaudeModel;
        _serverUrl = s.Ai.CustomServerUrl;
        _serverModel = s.Ai.CustomServerModel;
        _hasKey = !string.IsNullOrEmpty(Secrets.Unprotect(s.EncryptedApiKey));
        var t = s.Test;
        _cpuTempLimit = t.CpuTempLimit; _gpuTempLimit = t.GpuTempLimit; _stopOnError = t.StopOnError; _memoryPercent = t.MemoryPercent;
        _storageFileMB = t.StorageFileMB; _gpuFullscreen = t.GpuFullscreen; _reopenAfterCrash = t.ReopenAfterCrash; _laptopMode = t.LaptopMode;
        _init = false;
    }

    public ProviderOption[] Providers { get; }
    public ObservableCollection<LocalModelOption> LocalModels { get; } = new();
    [ObservableProperty] private ProviderOption _provider;
    [ObservableProperty] private LocalModelOption _selectedModel;
    [ObservableProperty] private string _claudeModel;
    [ObservableProperty] private string _serverUrl;
    [ObservableProperty] private string _serverModel;
    [ObservableProperty] private bool _hasKey;
    [ObservableProperty] private string _downloadStatus = "";
    [ObservableProperty] private double _downloadFraction;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private string _testResult = "";

    [ObservableProperty] private double _cpuTempLimit;
    [ObservableProperty] private double _gpuTempLimit;
    [ObservableProperty] private bool _stopOnError;
    [ObservableProperty] private double _memoryPercent;
    [ObservableProperty] private int _storageFileMB;
    [ObservableProperty] private bool _gpuFullscreen;
    [ObservableProperty] private bool _reopenAfterCrash;
    public LaptopMode[] LaptopModes { get; } = Enum.GetValues<LaptopMode>();
    [ObservableProperty] private LaptopMode _laptopMode;
    partial void OnLaptopModeChanged(LaptopMode value) { Save(); Main.Run.LaptopMode = value; }
    public string LaptopInfo => Main.S.Inventory is { } inv
        ? (inv.IsLaptop ? $"This PC is a laptop ({inv.ChassisType})" : $"This PC is not a laptop ({(inv.ChassisType.Length > 0 ? inv.ChassisType : "desktop")})") +
          (inv.BatteryWearPercent is double w ? $" · battery health {100 - w:0}%" : "")
        : "Detecting…";

    public bool IsLocal => Provider.Kind == AiProviderKind.LocalModel;
    public bool IsClaude => Provider.Kind == AiProviderKind.Claude;
    public bool IsServer => Provider.Kind == AiProviderKind.CustomServer;
    public string DataFolder => DataPaths.Root;
    public string PortableText => DataPaths.IsPortable ? "Portable mode: everything (results, settings, AI models) is stored next to BenchForge.exe — copy the folder to a USB stick and it comes along." : "The exe's folder is read-only, so data is stored in your user profile.";
    public string SensorText => Main.S.Lhm == null ? "Sensors are starting…" : Main.S.Lhm.PawnIoInstalled ? "PawnIO driver installed — all CPU sensors available." : "PawnIO driver not installed — CPU temperature/clock/power may be missing. Install it from pawnio.eu.";

    partial void OnProviderChanged(ProviderOption value)
    {
        OnPropertyChanged(nameof(IsLocal)); OnPropertyChanged(nameof(IsClaude)); OnPropertyChanged(nameof(IsServer));
        Save();
        if (!_init && value.Kind == AiProviderKind.LocalModel && !SelectedModel.IsDownloaded) _ = DownloadAsync();
    }

    /// <summary>Picking a model starts its download straight away.</summary>
    partial void OnSelectedModelChanged(LocalModelOption value)
    {
        Save();
        if (!_init && !value.IsDownloaded) _ = DownloadAsync();
    }

    partial void OnClaudeModelChanged(string value) => Save();
    partial void OnServerUrlChanged(string value) => Save();
    partial void OnServerModelChanged(string value) => Save();
    partial void OnCpuTempLimitChanged(double value) => Save();
    partial void OnGpuTempLimitChanged(double value) => Save();
    partial void OnStopOnErrorChanged(bool value) => Save();
    partial void OnMemoryPercentChanged(double value) => Save();
    partial void OnStorageFileMBChanged(int value) => Save();
    partial void OnGpuFullscreenChanged(bool value) => Save();
    partial void OnReopenAfterCrashChanged(bool value) => Save();

    private void Save()
    {
        if (_init) return;
        var s = Main.S.Settings;
        s.Ai.Provider = Provider.Kind;
        s.Ai.LocalModelId = SelectedModel.Info.Id;
        s.Ai.ClaudeModel = string.IsNullOrWhiteSpace(ClaudeModel) ? AiAnalyzer.DefaultModel : ClaudeModel.Trim();
        s.Ai.CustomServerUrl = ServerUrl;
        s.Ai.CustomServerModel = ServerModel;
        s.Test.CpuTempLimit = CpuTempLimit; s.Test.GpuTempLimit = GpuTempLimit; s.Test.StopOnError = StopOnError;
        s.Test.MemoryPercent = MemoryPercent; s.Test.StorageFileMB = StorageFileMB; s.Test.GpuFullscreen = GpuFullscreen; s.Test.ReopenAfterCrash = ReopenAfterCrash; s.Test.LaptopMode = LaptopMode;
        Main.S.SaveSettings();
    }

    public void SetApiKey(string key)
    {
        Main.S.Settings.EncryptedApiKey = string.IsNullOrWhiteSpace(key) ? null : Secrets.Protect(key.Trim());
        HasKey = !string.IsNullOrWhiteSpace(key);
        Main.S.SaveSettings();
    }

    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (IsDownloading) return;
        var model = SelectedModel;
        IsDownloading = true;
        _downloadCts = new CancellationTokenSource();
        var progress = new Progress<DownloadProgress>(p => { DownloadStatus = p.ToString(); DownloadFraction = p.Fraction; });
        try
        {
            await Task.Run(() => LocalAiManager.EnsureDownloadedAsync(model.Info, progress, _downloadCts.Token));
            model.IsDownloaded = true;
            DownloadStatus = $"{model.Info.Name} is ready. It runs entirely on this PC.";
        }
        catch (OperationCanceledException) { DownloadStatus = "Download paused — pick the model again to resume."; }
        catch (Exception ex) { DownloadStatus = "Download failed: " + ex.Message; }
        finally { IsDownloading = false; }
    }

    [RelayCommand] private void SelectProvider(ProviderOption? p) { if (p != null) Provider = p; }

    [RelayCommand] private void CancelDownload() => _downloadCts?.Cancel();

    [RelayCommand]
    private void DeleteModel()
    {
        if (MessageBox.Show($"Delete {SelectedModel.Info.Name} ({SelectedModel.Info.SizeBytes / 1e9:0.0} GB) from this folder?", "BenchForge", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        try { LocalAiManager.DeleteModel(SelectedModel.Info); SelectedModel.IsDownloaded = false; DownloadStatus = "Deleted."; }
        catch (Exception ex) { DownloadStatus = ex.Message; }
    }

    [RelayCommand]
    private async Task TestAi()
    {
        TestResult = "Asking…";
        var progress = new Progress<DownloadProgress>(p => TestResult = p.ToString());
        try
        {
            var answer = await Main.S.Ai.AskAsync(Main.S.AiSettings(), "Reply with one short sentence confirming you can help diagnose PC hardware problems.", Array.Empty<RunResult>(), null, null, progress, CancellationToken.None);
            TestResult = answer;
        }
        catch (Exception ex) { TestResult = "Failed: " + ex.Message; }
    }

    [RelayCommand] private void OpenFolder() { try { System.Diagnostics.Process.Start("explorer.exe", DataPaths.Root); } catch { } }
}

public sealed partial class AiChatViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly IReadOnlyList<RunResult> _runs;
    private readonly IReadOnlyList<Core.Crash.SystemCrash>? _crashes;
    private readonly IReadOnlyList<EventEntry>? _events;
    private CancellationTokenSource? _cts;

    public AiChatViewModel(MainViewModel main, string title, IReadOnlyList<RunResult> runs, IReadOnlyList<Core.Crash.SystemCrash>? crashes, IReadOnlyList<EventEntry>? events, string? question)
    {
        _main = main; _runs = runs; _crashes = crashes; _events = events;
        Title = title;
        _question = question ?? "";
        var s = main.S.Settings.Ai;
        ProviderText = "Using: " + AiService.Describe(s);
        IsConfigured = s.Provider != AiProviderKind.None;
        _contextPreview = AiAnalyzer.BuildContext(runs, crashes, events, 200_000);
    }

    public string Title { get; }
    public string ProviderText { get; }
    public bool IsConfigured { get; }
    [ObservableProperty] private string _question;
    [ObservableProperty] private string _answer = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _contextPreview;

    [RelayCommand]
    private async Task Ask()
    {
        if (!IsConfigured) { Status = "AI is off in Settings. Use 'Save file for another AI' and upload the file to Claude.ai or any AI chat instead."; return; }
        IsBusy = true;
        Answer = "";
        _cts = new CancellationTokenSource();
        var progress = new Progress<DownloadProgress>(p => Status = p.ToString());
        Status = "Thinking…";
        try
        {
            var settings = _main.S.AiSettings();
            Answer = await Task.Run(() => _main.S.Ai.AskAsync(settings, Question, _runs, _crashes, _events, progress, _cts.Token));
            Status = "Done.";
        }
        catch (OperationCanceledException) { Status = "Cancelled."; }
        catch (Exception ex) { Status = "Failed: " + ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand] private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void CopyContext()
    {
        try { Clipboard.SetText(ContextPreview); Status = "Copied — you can paste the data into any AI chat."; } catch { }
    }

    [RelayCommand]
    private void SaveForAi() => AiExportService.Export(_runs, _crashes, _events, Question);

    [RelayCommand]
    private void CopyAnswer()
    {
        try { Clipboard.SetText(Answer); Status = "Answer copied."; } catch { }
    }
}
