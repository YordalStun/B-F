using System.Collections.ObjectModel;
using System.ComponentModel;
using Component = BenchForge.Core.Model.Component;
using BenchForge.Core.Engine;
using BenchForge.Core.Model;
using BenchForge.Core.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BenchForge.App.ViewModels;

public sealed partial class PresetOption : ObservableObject
{
    public string Key { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string Glyph { get; init; } = "";
    [ObservableProperty] private bool _isSelected;
}

public sealed record StepPreview(string Name, string Kind, string Parts, string Duration);

public sealed partial class CustomStep : ObservableObject
{
    public string Name { get; init; } = "";
    public StepKind Kind { get; init; }
    public List<Component> Parts { get; init; } = new();
    public double Minutes { get; init; }
    public string PartsText => string.Join(" + ", Parts.Select(p => p.Display()));
    public string Text => $"{Name} — {PartsText} — {TimeFormat.Short(TimeSpan.FromMinutes(Minutes))}";
}

public sealed partial class RunSetupViewModel : PageViewModel
{
    public RunSetupViewModel(MainViewModel main) : base(main, "Run tests", "")
    {
        Presets = new()
        {
            new() { Key = "quick", Title = "Quick benchmark", Glyph = "", Description = "Scores CPU, RAM, drive and GPU in about 3½ minutes." },
            new() { Key = "full", Title = "Full benchmark", Glyph = "", Description = "Longer, more accurate scores plus a short stability check of every part (~16 min)." },
            new() { Key = "burn", Title = "Burn test", Glyph = "", Description = "Hammer the parts you pick at 100% at the same time for as long as you like." },
            new() { Key = "rotation", Title = "Stability rotation", Glyph = "", Description = "Each part alone, then all together, repeating — finds which part is unstable." },
            new() { Key = "overnight", Title = "Overnight", Glyph = "", Description = "Benchmark, hours of stability testing, then benchmark again to spot heat/ageing drops." },
            new() { Key = "single", Title = "One part", Glyph = "", Description = "Benchmark or stress just the CPU, RAM, drive or GPU." },
            new() { Key = "custom", Title = "Custom plan", Glyph = "", Description = "Build your own list of steps, and loop it." },
        };
        foreach (var p in Presets) p.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PresetOption.IsSelected) && p.IsSelected) { Selected = p; Rebuild(); } };
        Presets[0].IsSelected = true;
        Selected = Presets[0];

        var s = Main.S.Settings;
        _hours = s.BurnHours; _minutes = s.BurnMinutes;
        _burnCpu = s.BurnComponents.Contains(Component.Cpu); _burnGpu = s.BurnComponents.Contains(Component.Gpu);
        _burnRam = s.BurnComponents.Contains(Component.Memory); _burnDisk = s.BurnComponents.Contains(Component.Storage);
        var t = s.Test;
        _cpuStressMode = t.CpuStressMode; _memoryPercent = t.MemoryPercent; _gpuFullscreen = t.GpuFullscreen;
        _cpuTempLimit = t.CpuTempLimit; _gpuTempLimit = t.GpuTempLimit; _stopOnError = t.StopOnError; _reopenAfterCrash = t.ReopenAfterCrash; _storageFileMB = t.StorageFileMB;
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
            Drives.Add(d.Name);
        _storageTarget = Drives.Contains(t.StorageTarget) ? t.StorageTarget : Drives.FirstOrDefault(d => d == StorageModule.DefaultTarget()) ?? Drives.FirstOrDefault() ?? "";
        PropertyChanged += OnAnyChanged;
        Rebuild();
    }

    public ObservableCollection<PresetOption> Presets { get; }
    [ObservableProperty] private PresetOption _selected;

    // Durations
    [ObservableProperty] private int _hours;
    [ObservableProperty] private int _minutes;
    // Burn parts
    [ObservableProperty] private bool _burnCpu;
    [ObservableProperty] private bool _burnGpu;
    [ObservableProperty] private bool _burnRam;
    [ObservableProperty] private bool _burnDisk;
    // Single part
    public Component[] AllComponents { get; } = Enum.GetValues<Component>();
    public StepKind[] Kinds { get; } = Enum.GetValues<StepKind>();
    [ObservableProperty] private Component _singleComponent = Component.Cpu;
    [ObservableProperty] private StepKind _singleKind = StepKind.Stress;
    // Custom
    public ObservableCollection<CustomStep> CustomSteps { get; } = new();
    [ObservableProperty] private StepKind _newKind = StepKind.Stress;
    [ObservableProperty] private bool _newCpu = true;
    [ObservableProperty] private bool _newGpu;
    [ObservableProperty] private bool _newRam;
    [ObservableProperty] private bool _newDisk;
    [ObservableProperty] private double _newMinutes = 10;
    [ObservableProperty] private int _loops = 1;
    [ObservableProperty] private CustomStep? _selectedCustomStep;
    // Options
    public CpuStressMode[] StressModes { get; } = Enum.GetValues<CpuStressMode>();
    [ObservableProperty] private CpuStressMode _cpuStressMode;
    [ObservableProperty] private double _memoryPercent;
    public ObservableCollection<string> Drives { get; } = new();
    [ObservableProperty] private string _storageTarget;
    [ObservableProperty] private int _storageFileMB;
    [ObservableProperty] private bool _gpuFullscreen;
    [ObservableProperty] private double _cpuTempLimit;
    [ObservableProperty] private double _gpuTempLimit;
    [ObservableProperty] private bool _stopOnError;
    [ObservableProperty] private bool _reopenAfterCrash;
    [ObservableProperty] private bool _showOverlay = true;

    // Preview
    public ObservableCollection<StepPreview> Preview { get; } = new();
    [ObservableProperty] private string _totalText = "";
    [ObservableProperty] private string _finishText = "";
    [ObservableProperty] private bool _needsDuration;
    [ObservableProperty] private bool _needsParts;
    [ObservableProperty] private bool _isSingle;
    [ObservableProperty] private bool _isCustom;
    [ObservableProperty] private string? _planWarning;

    private bool _rebuilding;
    private void OnAnyChanged(object? s, PropertyChangedEventArgs e)
    {
        if (_rebuilding || e.PropertyName is nameof(TotalText) or nameof(FinishText) or nameof(PlanWarning) or nameof(NeedsDuration) or nameof(NeedsParts) or nameof(IsSingle) or nameof(IsCustom)) return;
        Rebuild();
    }

    private TestSettings BuildSettings() => new()
    {
        CpuStressMode = CpuStressMode, MemoryPercent = MemoryPercent, StorageTarget = StorageTarget, StorageFileMB = StorageFileMB,
        GpuFullscreen = GpuFullscreen, CpuTempLimit = CpuTempLimit, GpuTempLimit = GpuTempLimit, StopOnError = StopOnError, ReopenAfterCrash = ReopenAfterCrash,
    };

    private TimeSpan Duration => TimeSpan.FromHours(Math.Max(0, Hours)) + TimeSpan.FromMinutes(Math.Max(0, Minutes));

    private List<Component> BurnParts()
    {
        var l = new List<Component>();
        if (BurnCpu) l.Add(Component.Cpu);
        if (BurnRam) l.Add(Component.Memory);
        if (BurnGpu) l.Add(Component.Gpu);
        if (BurnDisk) l.Add(Component.Storage);
        return l;
    }

    public TestPlan? BuildPlan()
    {
        var s = BuildSettings();
        var dur = Duration.TotalMinutes < 1 ? TimeSpan.FromMinutes(1) : Duration;
        return Selected?.Key switch
        {
            "quick" => PlanPresets.QuickBenchmark(s),
            "full" => PlanPresets.FullBenchmark(s),
            "burn" => BurnParts().Count == 0 ? null : PlanPresets.BurnIn(dur, BurnParts(), s),
            "rotation" => PlanPresets.StabilityRotation(dur, s),
            "overnight" => PlanPresets.Overnight(dur, s),
            "single" => SingleKind == StepKind.Benchmark
                ? new TestPlan { Name = $"{SingleComponent.Display()} benchmark", Settings = s, Steps = { PlanPresets.FullBenchmark(s).Steps.First(x => x.Kind == StepKind.Benchmark && x.Components.Contains(SingleComponent)) } }
                : PlanPresets.SingleComponent(SingleComponent, StepKind.Stress, dur, s),
            "custom" => CustomSteps.Count == 0 ? null : new TestPlan
            {
                Name = "Custom plan", Settings = s, Loops = Math.Max(1, Loops),
                Steps = CustomSteps.Select(c => new TestStep(c.Name, c.Kind, c.Minutes * 60, c.Parts.ToArray())).ToList(),
            },
            _ => null,
        };
    }

    private void Rebuild()
    {
        _rebuilding = true;
        try
        {
            var key = Selected?.Key;
            NeedsDuration = key is "burn" or "rotation" or "overnight" || (key == "single" && SingleKind == StepKind.Stress);
            NeedsParts = key == "burn";
            IsSingle = key == "single";
            IsCustom = key == "custom";
            Preview.Clear();
            var plan = BuildPlan();
            PlanWarning = plan == null ? (key == "burn" ? "Pick at least one part to stress." : key == "custom" ? "Add at least one step." : null) : null;
            if (plan == null) { TotalText = ""; FinishText = ""; return; }
            var steps = plan.Expand();
            // Long plans: show the first steps and a summary line.
            foreach (var st in steps.Take(40))
                Preview.Add(new StepPreview(st.Name, st.Kind == StepKind.Benchmark ? "Benchmark" : "Stress", string.Join(" + ", st.Components.Select(c => c.Display())), TimeFormat.Short(TimeSpan.FromSeconds(st.DurationSeconds))));
            if (steps.Count > 40) Preview.Add(new StepPreview($"… and {steps.Count - 40} more steps", "", "", ""));
            var total = TimeSpan.FromSeconds(plan.TotalSeconds);
            TotalText = $"Takes {TimeFormat.Short(total)}";
            FinishText = $"Finishes around {DateTime.Now + total:HH:mm}{((DateTime.Now + total).Date != DateTime.Today ? $" on {(DateTime.Now + total):ddd d MMM}" : "")}";
        }
        finally { _rebuilding = false; }
    }

    [RelayCommand]
    private void AddStep()
    {
        var parts = new List<Component>();
        if (NewCpu) parts.Add(Component.Cpu);
        if (NewRam) parts.Add(Component.Memory);
        if (NewGpu) parts.Add(Component.Gpu);
        if (NewDisk) parts.Add(Component.Storage);
        if (parts.Count == 0) return;
        double minutes = NewKind == StepKind.Benchmark ? 0 : Math.Max(0.5, NewMinutes);
        if (NewKind == StepKind.Benchmark)
        {
            // Benchmarks have fixed lengths per part so results are comparable.
            foreach (var p in parts)
            {
                var b = PlanPresets.FullBenchmark().Steps.First(x => x.Kind == StepKind.Benchmark && x.Components.Contains(p));
                CustomSteps.Add(new CustomStep { Name = b.Name, Kind = StepKind.Benchmark, Parts = new() { p }, Minutes = b.DurationSeconds / 60 });
            }
        }
        else
        {
            CustomSteps.Add(new CustomStep { Name = parts.Count == 1 ? $"{parts[0].Display()} stress" : string.Join(" + ", parts.Select(p => p.Display())) + " stress", Kind = StepKind.Stress, Parts = parts, Minutes = minutes });
        }
        Rebuild();
    }

    [RelayCommand]
    private void RemoveStep()
    {
        if (SelectedCustomStep != null) CustomSteps.Remove(SelectedCustomStep);
        else if (CustomSteps.Count > 0) CustomSteps.RemoveAt(CustomSteps.Count - 1);
        Rebuild();
    }

    [RelayCommand]
    private void ClearSteps() { CustomSteps.Clear(); Rebuild(); }

    [RelayCommand]
    private void Start()
    {
        var plan = BuildPlan();
        if (plan == null) return;
        // Remember choices.
        var st = Main.S.Settings;
        st.Test = plan.Settings;
        st.BurnHours = Hours; st.BurnMinutes = Minutes;
        st.BurnComponents = BurnParts();
        Main.S.SaveSettings();
        Main.Live.ShowOverlayOnStart = ShowOverlay;
        Main.StartRun(plan);
    }

    public override void OnShown() => Rebuild();
}
