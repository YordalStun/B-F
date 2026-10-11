namespace BenchForge.Core.Model;

/// <summary>
/// Picks the handful of "headline" values (CPU temp, GPU load, …) out of the hundreds of raw sensors.
/// Works with LibreHardwareMonitor naming but falls back to fuzzy matching.
/// </summary>
public static class KeyMetrics
{
    public const string CpuTemp = "cpu.temp";
    public const string CpuLoad = "cpu.load";
    public const string CpuClock = "cpu.clock";
    public const string CpuPower = "cpu.power";
    public const string CpuVcore = "cpu.vcore";
    public const string GpuTemp = "gpu.temp";
    public const string GpuHotspot = "gpu.hotspot";
    public const string GpuMemTemp = "gpu.memtemp";
    public const string GpuLoad = "gpu.load";
    public const string GpuClock = "gpu.clock";
    public const string GpuMemClock = "gpu.memclock";
    public const string GpuPower = "gpu.power";
    public const string GpuVram = "gpu.vram";
    public const string GpuFan = "gpu.fan";
    public const string RamLoad = "ram.load";
    public const string RamUsed = "ram.used";
    public const string DiskTemp = "disk.temp";
    public const string DiskActivity = "disk.activity";
    public const string BoardTemp = "board.temp";
    public const string CpuFan = "fan.cpu";
    public const string Rail12V = "psu.12v";
    /// <summary>1 = charger connected, 0 = on battery.</summary>
    public const string OnAc = "psu.ac";
    public const string BatteryLevel = "battery.level";
    public const string AcReadingId = "/power/ac";

    public static readonly (string Id, string Label, string Unit)[] All =
    {
        (CpuTemp, "CPU temperature", "°C"),
        (CpuLoad, "CPU usage", "%"),
        (CpuClock, "CPU clock (avg)", "MHz"),
        (CpuPower, "CPU power", "W"),
        (CpuVcore, "CPU core voltage", "V"),
        (GpuTemp, "GPU temperature", "°C"),
        (GpuHotspot, "GPU hot spot", "°C"),
        (GpuMemTemp, "GPU memory temp", "°C"),
        (GpuLoad, "GPU usage", "%"),
        (GpuClock, "GPU core clock", "MHz"),
        (GpuMemClock, "GPU memory clock", "MHz"),
        (GpuPower, "GPU power", "W"),
        (GpuVram, "GPU memory used", "MB"),
        (GpuFan, "GPU fan", "RPM"),
        (RamLoad, "RAM usage", "%"),
        (RamUsed, "RAM used", "GB"),
        (DiskTemp, "Drive temperature (max)", "°C"),
        (DiskActivity, "Drive activity (max)", "%"),
        (BoardTemp, "Motherboard temperature", "°C"),
        (CpuFan, "CPU fan", "RPM"),
        (Rail12V, "+12V rail", "V"),
        (OnAc, "Charger", ""),
        (BatteryLevel, "Battery", "%"),
    };

    public static string Label(string id) => All.FirstOrDefault(a => a.Id == id).Label ?? id;
    public static string Unit(string id) => All.FirstOrDefault(a => a.Id == id).Unit ?? "";

    public static Dictionary<string, double> From(SensorSnapshot snap) => From(snap.Readings);

    /// <summary>Timeline channel id used to record a key metric.</summary>
    public static string ChannelId(string key) => "key/" + key;

    public static HwType HwTypeOf(string key) => key.Split('.')[0] switch
    {
        "cpu" => HwType.Cpu, "gpu" => HwType.Gpu, "ram" => HwType.Memory, "disk" => HwType.Storage, "board" or "fan" => HwType.Motherboard, "psu" or "battery" => HwType.Psu, _ => HwType.Other,
    };

    public static SensorKind KindOf(string key) => key switch
    {
        _ when key.EndsWith("temp") || key.EndsWith("hotspot") => SensorKind.Temperature,
        _ when key.EndsWith("load") || key.EndsWith("activity") => SensorKind.Load,
        _ when key.EndsWith("clock") => SensorKind.Clock,
        _ when key.EndsWith("power") => SensorKind.Power,
        _ when key.EndsWith("vcore") || key.EndsWith("12v") => SensorKind.Voltage,
        _ when key == OnAc => SensorKind.Other,
        _ when key == BatteryLevel => SensorKind.Level,
        _ when key.EndsWith("fan") || key.StartsWith("fan.") => SensorKind.Fan,
        _ => SensorKind.Data,
    };

    /// <summary>Key metrics as readings so they are recorded in the timeline next to the raw sensors.</summary>
    public static IEnumerable<SensorReading> AsReadings(IReadOnlyDictionary<string, double> key) =>
        key.Select(kv => new SensorReading(ChannelId(kv.Key), "Key metrics", HwTypeOf(kv.Key), KindOf(kv.Key), Label(kv.Key), kv.Value, Unit(kv.Key)));

    public static Dictionary<string, double> From(IReadOnlyList<SensorReading> readings)
    {
        var d = new Dictionary<string, double>();
        readings = readings.Where(r => !r.Id.StartsWith("key/") && !r.Id.StartsWith("test/")).ToList();
        var cpu = readings.Where(r => r.HwType == HwType.Cpu).ToList();
        var gpus = readings.Where(r => r.HwType == HwType.Gpu).GroupBy(r => r.Hardware).ToList();

        // CPU temperature: prefer package / Tctl, else hottest core.
        var cpuTemp = Pick(cpu, SensorKind.Temperature, "Core (Tctl/Tdie)", "CPU Package", "Package", "Tctl", "Tdie", "Core Max", "Core Average", "CPU")
                      ?? cpu.Where(r => r.Kind == SensorKind.Temperature && !r.Name.Contains("Distance", StringComparison.OrdinalIgnoreCase)).Select(r => (double?)r.Value).Max();
        Set(d, CpuTemp, cpuTemp);
        Set(d, CpuLoad, Pick(cpu, SensorKind.Load, "CPU Total", "Total"));
        var coreClocks = cpu.Where(r => r.Kind == SensorKind.Clock && (r.Name.StartsWith("Core #", StringComparison.OrdinalIgnoreCase) || r.Name.StartsWith("P-Core", StringComparison.OrdinalIgnoreCase) || r.Name.StartsWith("E-Core", StringComparison.OrdinalIgnoreCase)) && r.Value > 0).ToList();
        Set(d, CpuClock, coreClocks.Count > 0 ? coreClocks.Average(r => r.Value) : Pick(cpu, SensorKind.Clock, "Cores (Average)", "CPU Clock", "Clock"));
        Set(d, CpuPower, Pick(cpu, SensorKind.Power, "CPU Package", "Package", "Total"));
        Set(d, CpuVcore, Pick(cpu, SensorKind.Voltage, "Core (SVI2 TFN)", "CPU Core", "Core", "Vcore", "VID")
                         ?? Pick(readings.Where(r => r.HwType == HwType.Motherboard).ToList(), SensorKind.Voltage, "Vcore", "CPU Core", "Core"));

        // GPU: pick the busiest / discrete one.
        var gpu = gpus.OrderByDescending(g => g.Where(r => r.Kind == SensorKind.Load).Select(r => r.Value).DefaultIfEmpty(0).Max())
                      .ThenByDescending(g => g.Key.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                      .FirstOrDefault()?.ToList() ?? new List<SensorReading>();
        Set(d, GpuTemp, Pick(gpu, SensorKind.Temperature, "GPU Core", "GPU Temperature", "Core", "GPU"));
        Set(d, GpuHotspot, Pick(gpu, SensorKind.Temperature, "GPU Hot Spot", "Hot Spot", "Junction"));
        Set(d, GpuMemTemp, Pick(gpu, SensorKind.Temperature, "GPU Memory Junction", "GPU Memory", "Memory"));
        Set(d, GpuLoad, Pick(gpu, SensorKind.Load, "GPU Core", "D3D 3D", "GPU"));
        Set(d, GpuClock, Pick(gpu, SensorKind.Clock, "GPU Core", "Core"));
        Set(d, GpuMemClock, Pick(gpu, SensorKind.Clock, "GPU Memory", "Memory"));
        Set(d, GpuPower, Pick(gpu, SensorKind.Power, "GPU Package", "GPU Power", "Board Power", "GPU Core", "Total"));
        Set(d, GpuVram, Pick(gpu, SensorKind.Data, "GPU Memory Used", "D3D Dedicated Memory Used", "Memory Used"));
        Set(d, GpuFan, Pick(gpu, SensorKind.Fan, "GPU Fan", "GPU Fan 1", "Fan"));

        var mem = readings.Where(r => r.HwType == HwType.Memory).ToList();
        Set(d, RamLoad, Pick(mem, SensorKind.Load, "Memory", "Physical Memory", "Load"));
        Set(d, RamUsed, Pick(mem, SensorKind.Data, "Memory Used", "Used Memory", "Used"));

        var disks = readings.Where(r => r.HwType == HwType.Storage).ToList();
        Set(d, DiskTemp, disks.Where(r => r.Kind == SensorKind.Temperature).Select(r => (double?)r.Value).Max());
        Set(d, DiskActivity, disks.Where(r => r.Kind == SensorKind.Load && r.Name.Contains("Total Activity", StringComparison.OrdinalIgnoreCase)).Select(r => (double?)r.Value).Max());

        var board = readings.Where(r => r.HwType == HwType.Motherboard).ToList();
        Set(d, BoardTemp, Pick(board, SensorKind.Temperature, "System", "Motherboard", "Temperature #1"));
        Set(d, CpuFan, Pick(board, SensorKind.Fan, "CPU Fan", "CPU", "Fan #1", "Fan #2"));
        Set(d, Rail12V, Pick(board, SensorKind.Voltage, "+12V", "12V"));
        Set(d, OnAc, readings.FirstOrDefault(r => r.Id == AcReadingId)?.Value);
        Set(d, BatteryLevel, readings.Where(r => r.Kind == SensorKind.Level && r.Name.Contains("Charge Level", StringComparison.OrdinalIgnoreCase)).Select(r => (double?)r.Value).FirstOrDefault()
                             ?? readings.FirstOrDefault(r => r.Id == "/power/battery")?.Value);
        return d;
    }

    private static void Set(Dictionary<string, double> d, string id, double? v)
    {
        if (v is double x && !double.IsNaN(x) && !double.IsInfinity(x)) d[id] = x;
    }

    private static double? Pick(IReadOnlyList<SensorReading> list, SensorKind kind, params string[] names)
    {
        var ofKind = list.Where(r => r.Kind == kind).ToList();
        foreach (var n in names)
        {
            var exact = ofKind.FirstOrDefault(r => string.Equals(r.Name, n, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact.Value;
        }
        foreach (var n in names)
        {
            var partial = ofKind.FirstOrDefault(r => r.Name.Contains(n, StringComparison.OrdinalIgnoreCase));
            if (partial != null) return partial.Value;
        }
        return null;
    }
}
