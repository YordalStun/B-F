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

public sealed partial class DriveChoice : ObservableObject
{
    public string Drive { get; init; } = "";
    public string Text { get; init; } = "";
    [ObservableProperty] private bool _isChecked;
}

public sealed record GpuApiOption(string Key, string Name);

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
            new() { Key = "crash", Title = "Crash finder", Glyph = "\uE7BA", Description = "Finds crashes normal stress tests miss: each core alone at full boost, idle / light load, and power-supply load spikes." },
            new() { Key = "drive", Title = "Drive health check", Glyph = "\uEDA2", Description = "Deep drive test: benchmark, full surface scan for bad sectors and the write-cache test, per drive." },
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
        _gpuApi = GpuApis.FirstOrDefault(a => a.Key == t.GpuApi) ?? GpuApis[0];
        _laptopMode = t.LaptopMode;
        _cpuTempLimit = t.CpuTempLimit; _gpuTempLimit = t.GpuTempLimit; _stopOnError = t.StopOnError; _reopenAfterCrash = t.ReopenAfterCrash; _storageFileMB = t.StorageFileMB;
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            Drives.Add(d.Name);
            var label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "" : $" {d.VolumeLabel}";
            var dc = new DriveChoice { Drive = d.Name, Text = $"{d.Name.TrimEnd('\\')}{label} — {d.AvailableFreeSpace / 1e9:0} GB free of {d.TotalSize / 1e9:0} GB" };
            dc.PropertyChanged += (_, _) => Rebuild();
            DriveChoices.Add(dc);
        }
        _storageTarget = Drives.Contains(t.StorageTarget) ? t.StorageTarget : Drives.FirstOrDefault(d => d == StorageModule.DefaultTarget()) ?? Drives.FirstOrDefault() ?? "";
        foreach (var dc in DriveChoices) dc.IsChecked = dc.Drive == _storageTarget;
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
    // Crash finder
    [ObservableProperty] private bool _crashCoreCycle = true;
    [ObservableProperty] private bool _crashLightLoad = true;
    [ObservableProperty] private bool _crashPsuSpikes = true;
    [ObservableProperty] private bool _isCrash;
    // Drive health check
    public ObservableCollection<DriveChoice> DriveChoices { get; } = new();
    [ObservableProperty] private int _driveMinutes = 20;
    [ObservableProperty] private bool _isDrive;
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
    public GpuApiOption[] GpuApis { get; } =
    {
        new(Core.Gpu.GpuApis.OpenGL, "OpenGL 4.3"),
        new(Core.Gpu.GpuApis.D3D12, "DirectX 12"),
        new(Core.Gpu.GpuApis.D3D11, "DirectX 11"),
        new(Core.Gpu.GpuApis.Vulkan, "Vulkan"),
        new("all", "All of them (compare)"),
    };
    [ObservableProperty] private GpuApiOption _gpuApi = null!;
    [ObservableProperty] private double _cpuTempLimit;
    [ObservableProperty] private double _gpuTempLimit;
    [ObservableProperty] private bool _stopOnError;
    [ObservableProperty] private bool _reopenAfterCrash;
    [ObservableProperty] private bool _showOverlay = true;
    public LaptopMode[] LaptopModes { get; } = Enum.GetValues<LaptopMode>();
    [ObservableProperty] private LaptopMode _laptopMode;
    [ObservableProperty] private bool _laptopDetected;
    [ObservableProperty] private string? _laptopText;
    public bool LaptopRules => LaptopMode == LaptopMode.On || (LaptopMode == LaptopMode.Auto && LaptopDetected);

    /// <summary>Called when the hardware inventory is ready.</summary>
    public void OnInventory(HardwareInventory inv)
    {
        LaptopDetected = inv.IsLaptop;
        UpdateLaptopText();
        // Laptops sit at their thermal limit by design: raise the default safety limits (only if still at desktop defaults).
        if (LaptopRules && CpuTempLimit == 95 && GpuTempLimit == 90) { CpuTempLimit = 100; GpuTempLimit = 95; }
    }

    partial void OnLaptopModeChanged(LaptopMode value)
    {
        UpdateLaptopText();
        if (LaptopRules && CpuTempLimit == 95 && GpuTempLimit == 90) { CpuTempLimit = 100; GpuTempLimit = 95; }
        else if (!LaptopRules && CpuTempLimit == 100 && GpuTempLimit == 95) { CpuTempLimit = 95; GpuTempLimit = 90; }
    }

    private void UpdateLaptopText() => LaptopText = LaptopRules
        ? (LaptopDetected ? "Laptop detected — laptop mode is on." : "Laptop mode is on.") + " Running at the thermal limit is treated as normal; BenchForge judges how much performance the cooling keeps instead. Keep the charger plugged in."
        : LaptopDetected ? "Laptop detected, but laptop mode is off — hot-but-normal temperatures will be flagged as problems." : null;

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
        if (_rebuilding || e.PropertyName is nameof(TotalText) or nameof(FinishText) or nameof(PlanWarning) or nameof(NeedsDuration) or nameof(NeedsParts) or nameof(IsSingle) or nameof(IsCustom) or nameof(IsDrive) or nameof(IsCrash)) return;
        Rebuild();
    }

    private TestSettings BuildSettings() => new()
    {
        CpuStressMode = CpuStressMode, MemoryPercent = MemoryPercent, StorageTarget = StorageTarget, StorageFileMB = StorageFileMB,
        GpuFullscreen = GpuFullscreen, GpuApi = GpuApi.Key, CpuTempLimit = CpuTempLimit, GpuTempLimit = GpuTempLimit, StopOnError = StopOnError, ReopenAfterCrash = ReopenAfterCrash,
        LaptopMode = LaptopMode, LaptopRules = LaptopRules,
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
            "crash" => CrashCoreCycle || CrashLightLoad || CrashPsuSpikes ? PlanPresets.CrashFinder(dur, CrashCoreCycle, CrashLightLoad, CrashPsuSpikes, s) : null,
            "drive" => DriveChoices.Any(d => d.IsChecked)
                ? PlanPresets.DriveHealthCheck(TimeSpan.FromMinutes(Math.Max(3, DriveMinutes)), DriveChoices.Where(d => d.IsChecked).Select(d => d.Drive).ToList(), s)
                : null,
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
            NeedsDuration = key is "burn" or "rotation" or "overnight" or "crash" || (key == "single" && SingleKind == StepKind.Stress);
            NeedsParts = key == "burn";
            IsSingle = key == "single";
            IsCustom = key == "custom";
            IsDrive = key == "drive";
            IsCrash = key == "crash";
            Preview.Clear();
            var plan = BuildPlan();
            PlanWarning = plan == null ? (key == "burn" ? "Pick at least one part to stress." : key == "custom" ? "Add at least one step." : key == "drive" ? "Pick at least one drive." : key == "crash" ? "Pick at least one test." : null) : null;
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
        var (onAc, hasBattery, pct) = Core.Platform.PowerStatus.Get();
        if (hasBattery && onAc == false &&
            System.Windows.MessageBox.Show($"This PC is running on battery ({pct}%).\n\nOn battery, laptops limit power, so scores and stress tests will be lower and a long test may drain the battery.\n\nPlug in the charger for accurate results. Start anyway?",
                "Charger not connected", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.Yes)
            return;
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
