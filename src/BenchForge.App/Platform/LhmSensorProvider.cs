using System.Diagnostics;
using System.Management;
using BenchForge.Core.Model;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

namespace BenchForge.App.Platform;

/// <summary>Reads every sensor LibreHardwareMonitor can see (CPU, GPU, RAM, drives, motherboard, PSU, coolers).</summary>
public sealed class LhmSensorProvider : ISensorProvider
{
    private readonly Computer _computer;
    private readonly object _lock = new();
    private PerformanceCounter? _cpuCounter;
    private bool _wmiThermalFailed;

    public string? Warning { get; private set; }
    public bool PawnIoInstalled { get; }

    public LhmSensorProvider()
    {
        PawnIoInstalled = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO") != null;
        _computer = new Computer
        {
            IsCpuEnabled = true, IsGpuEnabled = true, IsMemoryEnabled = true, IsMotherboardEnabled = true,
            IsStorageEnabled = true, IsControllerEnabled = true, IsPsuEnabled = true, IsBatteryEnabled = true, IsNetworkEnabled = false,
        };
        try { _computer.Open(); }
        catch (Exception ex) { Warning = "Hardware sensors could not start: " + ex.Message; }
        if (!PawnIoInstalled)
            Warning = (Warning != null ? Warning + " " : "") +
                      "CPU temperature, clock and power sensors need the free PawnIO driver (pawnio.eu). GPU, drive and RAM sensors work without it.";
    }

    public SensorSnapshot Read()
    {
        var snap = new SensorSnapshot { Time = DateTime.Now };
        lock (_lock)
        {
            foreach (var hw in _computer.Hardware)
            {
                try
                {
                    hw.Update();
                    Collect(hw, Map(hw.HardwareType), snap.Readings);
                    foreach (var sub in hw.SubHardware)
                    {
                        sub.Update();
                        Collect(sub, hw.HardwareType == HardwareType.Motherboard ? HwType.Motherboard : Map(hw.HardwareType), snap.Readings);
                    }
                }
                catch { /* a misbehaving device must not stop the others */ }
            }
        }
        AddFallbacks(snap);
        return snap;
    }

    private static void Collect(IHardware hw, HwType type, List<SensorReading> list)
    {
        foreach (var s in hw.Sensors)
        {
            if (s.Value is not float v || float.IsNaN(v) || float.IsInfinity(v)) continue;
            var (kind, unit) = s.SensorType switch
            {
                SensorType.Temperature => (SensorKind.Temperature, "°C"),
                SensorType.Load => (SensorKind.Load, "%"),
                SensorType.Clock => (SensorKind.Clock, "MHz"),
                SensorType.Power => (SensorKind.Power, "W"),
                SensorType.Fan => (SensorKind.Fan, "RPM"),
                SensorType.Voltage => (SensorKind.Voltage, "V"),
                SensorType.Current => (SensorKind.Current, "A"),
                SensorType.Data => (SensorKind.Data, "GB"),
                SensorType.SmallData => (SensorKind.Data, "MB"),
                SensorType.Throughput => (SensorKind.Throughput, "MB/s"),
                SensorType.Frequency => (SensorKind.Frequency, "Hz"),
                SensorType.Level => (SensorKind.Level, "%"),
                SensorType.Control => (SensorKind.Level, "%"),
                _ => (SensorKind.Other, ""),
            };
            double value = s.SensorType == SensorType.Throughput ? v / 1e6 : v;
            list.Add(new SensorReading(s.Identifier.ToString(), hw.Name, type, kind, s.Name, Math.Round(value, 3), unit));
        }
    }

    private static HwType Map(HardwareType t) => t switch
    {
        HardwareType.Cpu => HwType.Cpu,
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => HwType.Gpu,
        HardwareType.Memory => HwType.Memory,
        HardwareType.Storage => HwType.Storage,
        HardwareType.Motherboard or HardwareType.SuperIO or HardwareType.EmbeddedController => HwType.Motherboard,
        HardwareType.Network => HwType.Network,
        HardwareType.Psu => HwType.Psu,
        HardwareType.Cooler => HwType.Cooler,
        _ => HwType.Other,
    };

    /// <summary>When LHM can't read CPU load/temperature (no driver), fall back to Windows counters / ACPI thermal zone.</summary>
    private void AddFallbacks(SensorSnapshot snap)
    {
        if (!snap.Readings.Any(r => r.HwType == HwType.Cpu && r.Kind == SensorKind.Load && r.Name.Contains("Total")))
        {
            try
            {
                _cpuCounter ??= new PerformanceCounter("Processor Information", "% Processor Utility", "_Total");
                var v = Math.Min(100, _cpuCounter.NextValue());
                snap.Readings.Add(new SensorReading("/fallback/cpu/load", "CPU", HwType.Cpu, SensorKind.Load, "CPU Total", Math.Round(v, 1), "%"));
            }
            catch { }
        }
        if (!_wmiThermalFailed && !snap.Readings.Any(r => r.HwType == HwType.Cpu && r.Kind == SensorKind.Temperature))
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentTemperature, InstanceName FROM MSAcpi_ThermalZoneTemperature");
                double max = 0;
                foreach (ManagementObject mo in searcher.Get())
                    max = Math.Max(max, Convert.ToDouble(mo["CurrentTemperature"]) / 10.0 - 273.15);
                if (max > 0 && max < 130)
                    snap.Readings.Add(new SensorReading("/fallback/acpi/temp", "ACPI thermal zone", HwType.Cpu, SensorKind.Temperature, "CPU (thermal zone)", Math.Round(max, 1), "°C"));
            }
            catch { _wmiThermalFailed = true; }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            try { _computer.Close(); } catch { }
        }
        _cpuCounter?.Dispose();
    }
}

/// <summary>
/// Owns the sensor provider and keeps one fresh snapshot per second for everybody (dashboard and test runner),
/// so the hardware is never read twice at the same time.
/// </summary>
public sealed class SensorHub : ISensorProvider
{
    private readonly ISensorProvider _inner;
    private SensorSnapshot _latest = new();
    private Dictionary<string, double> _latestKey = new();
    private readonly object _lock = new();
    private readonly CancellationTokenSource _cts = new();

    public SensorHub(ISensorProvider inner)
    {
        _inner = inner;
        Task.Run(Loop);
    }

    public event Action<SensorSnapshot, IReadOnlyDictionary<string, double>>? Updated;
    public string? Warning => _inner.Warning;
    public ISensorProvider Inner => _inner;
    public SensorSnapshot Latest { get { lock (_lock) return _latest; } }
    public IReadOnlyDictionary<string, double> LatestKey { get { lock (_lock) return _latestKey; } }

    private async Task Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var s = _inner.Read();
                var k = KeyMetrics.From(s);
                lock (_lock) { _latest = s; _latestKey = k; }
                Updated?.Invoke(s, k);
            }
            catch (Exception ex) { Core.Persistence.DataPaths.AppLogWrite("Sensor loop: " + ex.Message); }
            var wait = 1000 - (int)sw.ElapsedMilliseconds;
            if (wait > 0) { try { await Task.Delay(wait, _cts.Token); } catch { } }
        }
    }

    /// <summary>Returns a copy of the latest snapshot (the runner adds its own readings to it).</summary>
    public SensorSnapshot Read()
    {
        lock (_lock) return new SensorSnapshot { Time = DateTime.Now, Readings = _latest.Readings.ToList() };
    }

    public void Dispose()
    {
        _cts.Cancel();
        _inner.Dispose();
    }
}
