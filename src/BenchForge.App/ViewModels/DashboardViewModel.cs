using System.Collections.ObjectModel;
using BenchForge.Core.Model;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BenchForge.App.ViewModels;

public sealed partial class SensorRow : ObservableObject
{
    public string Id { get; init; } = "";
    public string Hardware { get; init; } = "";
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Unit { get; init; } = "";
    [ObservableProperty] private string _value = "";
    [ObservableProperty] private string _min = "";
    [ObservableProperty] private string _max = "";
    public double MinV = double.MaxValue, MaxV = double.MinValue;
}

public sealed partial class DashboardViewModel : PageViewModel
{
    private readonly DateTime _start = DateTime.Now;
    private readonly Dictionary<string, SensorRow> _rows = new();

    public DashboardViewModel(MainViewModel main) : base(main, "Dashboard", "")
    {
        foreach (var (id, label, unit) in KeyMetrics.All) Tiles.Add(new MetricTile(id, label, unit));
        Charts = MetricChart.Standard();
    }

    public ObservableCollection<MetricTile> Tiles { get; } = new();
    public MetricChart[] Charts { get; }
    public ObservableCollection<SensorRow> AllSensors { get; } = new();
    public ObservableCollection<Recommendation> SystemChecks { get; } = new();
    [ObservableProperty] private string _crashSummary = "";

    /// <summary>Problems that can be spotted without running a test (settings, drive health, recent crashes).</summary>
    public void LoadSystemChecks(HardwareInventory inv)
    {
        SystemChecks.Clear();
        foreach (var r in Core.Analysis.RecommendationEngine.SystemChecks(inv)) SystemChecks.Add(r);
        Task.Run(() =>
        {
            try
            {
                var spec = new Core.Events.EventQuerySpec { Since = DateTime.Now.AddDays(-30) };
                spec.Selectors.AddRange(Core.Events.EventPresets.BlueScreens.Selectors);
                spec.Selectors.AddRange(Core.Events.EventPresets.UnexpectedShutdowns.Selectors);
                spec.Selectors.AddRange(Core.Events.EventPresets.Whea.Selectors);
                var events = Main.S.Events.Query(spec, 500);
                var crashes = Core.Crash.CrashAnalyzer.FindSystemCrashes(events);
                int whea = events.Count(e => e.Provider.Contains("WHEA"));
                var text = crashes.Count == 0 && whea == 0 ? "No crashes or hardware errors recorded by Windows in the last 30 days."
                    : $"Windows recorded {crashes.Count} crash{(crashes.Count == 1 ? "" : "es")} and {whea} hardware (WHEA) error{(whea == 1 ? "" : "s")} in the last 30 days — see Crashes.";
                Ui.Post(() => CrashSummary = text);
            }
            catch { }
        });
    }

    [ObservableProperty] private string _cpuName = "Detecting hardware…";
    [ObservableProperty] private string _gpuName = "";
    [ObservableProperty] private string _ramText = "";
    [ObservableProperty] private string _boardText = "";
    [ObservableProperty] private string _osText = "";
    [ObservableProperty] private string _drivesText = "";
    [ObservableProperty] private string? _sensorWarning;
    [ObservableProperty] private string _sensorFilter = "";
    [ObservableProperty] private int _sensorCount;

    public void SetInventory(HardwareInventory inv)
    {
        CpuName = $"{inv.Cpu.Name}  ·  {inv.Cpu.Cores} cores / {inv.Cpu.Threads} threads{(inv.Cpu.InstructionSets.Length > 0 ? "  ·  " + inv.Cpu.InstructionSets : "")}";
        GpuName = string.Join("   |   ", inv.Gpus.Select(g => $"{g.Name}{(g.VramGB > 0 ? $" {g.VramGB:0.#} GB" : "")} · driver {g.DriverVersion}"));
        int speed = inv.MemoryModules.Select(m => m.ConfiguredSpeedMTs).DefaultIfEmpty(0).Max();
        RamText = $"{inv.TotalMemoryGB:0.#} GB {inv.MemoryModules.FirstOrDefault()?.Type} · {inv.MemoryModules.Count} module{(inv.MemoryModules.Count == 1 ? "" : "s")}{(speed > 0 ? $" @ {speed} MT/s" : "")}";
        BoardText = $"{inv.Motherboard} · BIOS {inv.BiosVersion}{(inv.BiosDate is DateTime d ? $" ({d:yyyy-MM-dd})" : "")}";
        OsText = $"{inv.OsName} {inv.OsVersion} · power plan: {inv.PowerPlan}";
        DrivesText = string.Join("   |   ", inv.Disks.Select(d => $"{d.Model} {d.SizeGB:0} GB {d.BusType} {d.MediaType} ({d.Health})"));
    }

    public void OnSample(SensorSnapshot snap, IReadOnlyDictionary<string, double> key)
    {
        double t = (snap.Time - _start).TotalSeconds;
        foreach (var tile in Tiles)
            if (key.TryGetValue(tile.Id, out var v)) tile.Push(v);
        foreach (var c in Charts) c.Push(t, key, 600);

        foreach (var r in snap.Readings)
        {
            if (!_rows.TryGetValue(r.Id, out var row))
            {
                row = new SensorRow { Id = r.Id, Hardware = r.Hardware, Name = r.Name, Kind = r.Kind.ToString(), Unit = r.Unit };
                _rows[r.Id] = row;
                if (Matches(row)) InsertSorted(row);
            }
            row.MinV = Math.Min(row.MinV, r.Value); row.MaxV = Math.Max(row.MaxV, r.Value);
            row.Value = MetricTile.Format(r.Value, r.Unit);
            row.Min = MetricTile.Format(row.MinV, r.Unit);
            row.Max = MetricTile.Format(row.MaxV, r.Unit);
        }
        SensorCount = _rows.Count;
    }

    private bool Matches(SensorRow r) => string.IsNullOrWhiteSpace(SensorFilter) ||
        r.Hardware.Contains(SensorFilter, StringComparison.OrdinalIgnoreCase) || r.Name.Contains(SensorFilter, StringComparison.OrdinalIgnoreCase) || r.Kind.Contains(SensorFilter, StringComparison.OrdinalIgnoreCase);

    private void InsertSorted(SensorRow row)
    {
        int i = 0;
        while (i < AllSensors.Count && string.Compare(AllSensors[i].Hardware + AllSensors[i].Kind, row.Hardware + row.Kind, StringComparison.Ordinal) <= 0) i++;
        AllSensors.Insert(i, row);
    }

    partial void OnSensorFilterChanged(string value)
    {
        AllSensors.Clear();
        foreach (var r in _rows.Values.Where(Matches).OrderBy(r => r.Hardware).ThenBy(r => r.Kind).ThenBy(r => r.Name)) AllSensors.Add(r);
    }

    [RelayCommand]
    private void OpenPawnIo()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://pawnio.eu") { UseShellExecute = true }); } catch { }
    }
}
