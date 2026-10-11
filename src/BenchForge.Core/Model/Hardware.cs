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
}
