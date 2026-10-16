namespace BenchForge.Core.Model;

public sealed class HardwareInventory
{
    public DateTime Collected { get; set; } = DateTime.Now;
    public string ComputerName { get; set; } = Environment.MachineName;
    public string OsName { get; set; } = "";
    public string OsVersion { get; set; } = "";
    public DateTime? LastBoot { get; set; }
    public string PowerPlan { get; set; } = "";
    public CpuInfo Cpu { get; set; } = new();
    public List<GpuInfo> Gpus { get; set; } = new();
    public List<MemoryModuleInfo> MemoryModules { get; set; } = new();
    public double TotalMemoryGB { get; set; }
    public List<DiskInfo> Disks { get; set; } = new();
    public string Motherboard { get; set; } = "";
    public string BiosVersion { get; set; } = "";
    public DateTime? BiosDate { get; set; }
    public bool IsAdmin { get; set; }
    public bool IsLaptop { get; set; }
    public string ChassisType { get; set; } = "";
    public bool HasBattery { get; set; }
    public bool? OnBattery { get; set; }
    public double? BatteryDesignCapacityWh { get; set; }
    public double? BatteryFullChargeWh { get; set; }
    /// <summary>How much capacity the battery has lost, 0–100 %.</summary>
    public double? BatteryWearPercent { get; set; }
    /// <summary>CrashControl\CrashDumpEnabled: 0 = none, 1 = complete, 2 = kernel, 3 = small, 7 = automatic.</summary>
    public int? CrashDumpSetting { get; set; }
    public bool? FastStartupEnabled { get; set; }
    public int? TdrDelaySeconds { get; set; }
    /// <summary>TRIM enabled for NTFS (fsutil DisableDeleteNotify = 0).</summary>
    public bool? TrimEnabled { get; set; }
    public List<PcieLink> PcieLinks { get; set; } = new();
    public List<DisplayInfo> Displays { get; set; } = new();
    public List<NetAdapterInfo> NetworkAdapters { get; set; } = new();
    public List<UsbDeviceInfo> UsbDevices { get; set; } = new();
    public List<string> Notes { get; set; } = new();
}

public sealed class CpuInfo
{
    public string Name { get; set; } = "";
    public int Cores { get; set; }
    public int Threads { get; set; } = Environment.ProcessorCount;
    public int MaxClockMHz { get; set; }
    public string Socket { get; set; } = "";
    public int L2KB { get; set; }
    public int L3KB { get; set; }
    public string InstructionSets { get; set; } = "";
}

public sealed class GpuInfo
{
    public string Name { get; set; } = "";
    public double VramGB { get; set; }
    public string DriverVersion { get; set; } = "";
    public DateTime? DriverDate { get; set; }
    public string Resolution { get; set; } = "";
    public int RefreshRate { get; set; }
}

public sealed class MemoryModuleInfo
{
    public string Slot { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string PartNumber { get; set; } = "";
    public double CapacityGB { get; set; }
    public int SpeedMTs { get; set; }
    public int ConfiguredSpeedMTs { get; set; }
    public string Type { get; set; } = "";
}

public sealed class DiskInfo
{
    public string Model { get; set; } = "";
    public double SizeGB { get; set; }
    public string MediaType { get; set; } = "";   // SSD / HDD / Unknown
    public string BusType { get; set; } = "";     // NVMe / SATA / USB …
    public string Health { get; set; } = "";      // Healthy / Warning / Unhealthy
    public bool? SmartPredictFailure { get; set; }
    public double? TemperatureC { get; set; }
    public double? WearPercent { get; set; }
    public long? PowerOnHours { get; set; }
    public long? ReadErrors { get; set; }
    public long? WriteErrors { get; set; }
    public List<string> Volumes { get; set; } = new();
    public double? FreePercent { get; set; }
    public int? DiskNumber { get; set; }
    /// <summary>SATA SMART attributes (empty for NVMe / USB).</summary>
    public List<SmartAttribute> Smart { get; set; } = new();
    public NvmeHealthInfo? Nvme { get; set; }
    /// <summary>A partition does not start on a 4 KB boundary (slows SSDs).</summary>
    public bool? Misaligned { get; set; }
    /// <summary>Which partitions are misaligned, e.g. "C: (partition 2) starts at 32,256 bytes".</summary>
    public List<string> MisalignedPartitions { get; set; } = new();
}

public sealed record SmartAttribute(int Id, string Name, int Current, int Worst, int? Threshold, long Raw)
{
    public bool Failing => Threshold is int t && t > 0 && Current <= t;
}

public sealed class NvmeHealthInfo
{
    public int CriticalWarning { get; set; }
    public int TemperatureC { get; set; }
    public int AvailableSparePercent { get; set; }
    public int SpareThresholdPercent { get; set; }
    /// <summary>Wear: 100 % = rated endurance reached (can go above 100).</summary>
    public int PercentageUsed { get; set; }
    public double DataReadTB { get; set; }
    public double DataWrittenTB { get; set; }
    public long PowerCycles { get; set; }
    public long PowerOnHours { get; set; }
    public long UnsafeShutdowns { get; set; }
    public long MediaErrors { get; set; }
    public long ErrorLogEntries { get; set; }
}

/// <summary>Negotiated vs maximum PCI Express link of a device (NVMe controller, graphics card).</summary>
public sealed record PcieLink(string Device, string Kind, int CurrentGen, int CurrentWidth, int MaxGen, int MaxWidth)
{
    public bool Degraded => CurrentGen > 0 && MaxGen > 0 && (CurrentGen < MaxGen || CurrentWidth < MaxWidth);
    public override string ToString() => $"PCIe {CurrentGen}.0 ×{CurrentWidth} (supports {MaxGen}.0 ×{MaxWidth})";
}

public sealed class DisplayInfo
{
    public string Name { get; set; } = "";
    public string Adapter { get; set; } = "";
    public bool Primary { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int CurrentHz { get; set; }
    /// <summary>Highest refresh rate offered at the current resolution.</summary>
    public int MaxHzAtCurrentResolution { get; set; }
    /// <summary>Highest refresh rate offered at any resolution.</summary>
    public int MaxHz { get; set; }
    public int MaxWidth { get; set; }
    public int MaxHeight { get; set; }
}

public sealed class NetAdapterInfo
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>"Wired" or "Wi-Fi".</summary>
    public string Kind { get; set; } = "";
    public bool Connected { get; set; }
    public double? LinkMbps { get; set; }
    public int? WifiSignalPercent { get; set; }
    public string? WifiBand { get; set; }
    public string? WifiStandard { get; set; }
}

public sealed class UsbDeviceInfo
{
    public string Name { get; set; } = "";
    public string VidPid { get; set; } = "";
    /// <summary>Speed it is running at: "USB 1 Low", "USB 1 Full", "USB 2 High", "USB 3 SuperSpeed", "USB 3 SuperSpeed+".</summary>
    public string Speed { get; set; } = "";
    public bool SuperSpeedCapable { get; set; }
    public bool RunningAtSuperSpeed { get; set; }
    public bool Degraded => SuperSpeedCapable && !RunningAtSuperSpeed;
}

public sealed class NetworkTargetStats
{
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public int Sent { get; set; }
    public int Received { get; set; }
    public double SumMs { get; set; }
    public double MaxMs { get; set; }
    public int Dropouts { get; set; }
    /// <summary>Pinging was not possible at all (blocked) — no conclusions.</summary>
    public bool Unavailable { get; set; }
    public double LossPercent => Sent == 0 ? 0 : 100.0 * (Sent - Received) / Sent;
    public double AvgMs => Received == 0 ? 0 : SumMs / Received;
}
