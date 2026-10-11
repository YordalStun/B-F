using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Security.Principal;
using BenchForge.Core.Model;
using Microsoft.Win32;

namespace BenchForge.App.Platform;

/// <summary>Collects what hardware is installed (WMI + registry).</summary>
public static class WmiInventory
{
    public static HardwareInventory Collect()
    {
        var inv = new HardwareInventory
        {
            IsAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator),
        };
        Try(inv, "CPU", () =>
        {
            foreach (var o in Query("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, SocketDesignation, L2CacheSize, L3CacheSize FROM Win32_Processor"))
            {
                inv.Cpu.Name = Str(o, "Name").Trim();
                inv.Cpu.Cores += Int(o, "NumberOfCores");
                inv.Cpu.Threads = Environment.ProcessorCount;
                inv.Cpu.MaxClockMHz = Int(o, "MaxClockSpeed");
                inv.Cpu.Socket = Str(o, "SocketDesignation");
                inv.Cpu.L2KB = Int(o, "L2CacheSize");
                inv.Cpu.L3KB = Int(o, "L3CacheSize");
            }
            var sets = new List<string>();
            if (System.Runtime.Intrinsics.X86.Avx2.IsSupported) sets.Add("AVX2");
            if (System.Runtime.Intrinsics.X86.Avx512F.IsSupported) sets.Add("AVX-512");
            if (System.Runtime.Intrinsics.X86.Aes.IsSupported) sets.Add("AES-NI");
            if (System.Runtime.Intrinsics.X86.Fma.IsSupported) sets.Add("FMA3");
            inv.Cpu.InstructionSets = string.Join(", ", sets);
        });
        Try(inv, "GPU", () =>
        {
            var vramByName = GpuMemoryFromRegistry();
            foreach (var o in Query("SELECT Name, AdapterRAM, DriverVersion, DriverDate, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate FROM Win32_VideoController"))
            {
                var name = Str(o, "Name");
                double vram = vramByName.TryGetValue(name, out var q) ? q / 1073741824.0 : Long(o, "AdapterRAM") / 1073741824.0;
                inv.Gpus.Add(new GpuInfo
                {
                    Name = name, VramGB = Math.Round(vram, 1), DriverVersion = Str(o, "DriverVersion"), DriverDate = CimDate(Str(o, "DriverDate")),
                    Resolution = Int(o, "CurrentHorizontalResolution") > 0 ? $"{Int(o, "CurrentHorizontalResolution")}×{Int(o, "CurrentVerticalResolution")}" : "",
                    RefreshRate = Int(o, "CurrentRefreshRate"),
                });
            }
        });
        Try(inv, "RAM", () =>
        {
            foreach (var o in Query("SELECT BankLabel, DeviceLocator, Manufacturer, PartNumber, Capacity, Speed, ConfiguredClockSpeed, SMBIOSMemoryType FROM Win32_PhysicalMemory"))
            {
                inv.MemoryModules.Add(new MemoryModuleInfo
                {
                    Slot = Str(o, "DeviceLocator"), Manufacturer = Str(o, "Manufacturer").Trim(), PartNumber = Str(o, "PartNumber").Trim(),
                    CapacityGB = Long(o, "Capacity") / 1073741824.0, SpeedMTs = Int(o, "Speed"), ConfiguredSpeedMTs = Int(o, "ConfiguredClockSpeed"),
                    Type = Int(o, "SMBIOSMemoryType") switch { 26 => "DDR4", 34 => "DDR5", 24 => "DDR3", 30 => "LPDDR4", 35 => "LPDDR5", _ => "" },
                });
            }
            inv.TotalMemoryGB = Math.Round(Core.Platform.MemoryStatus.Get().Total / 1073741824.0, 1);
        });
        Try(inv, "Drives", () =>
        {
            var letters = new Dictionary<string, List<string>>();
            foreach (var p in Query("SELECT DiskNumber, DriveLetter FROM MSFT_Partition", @"root\Microsoft\Windows\Storage"))
            {
                var dl = o2s(p["DriveLetter"]);
                if (string.IsNullOrWhiteSpace(dl) || dl == "\0") continue;
                var key = o2s(p["DiskNumber"]);
                if (!letters.TryGetValue(key, out var l)) letters[key] = l = new();
                l.Add(dl.Trim('\0') + ":\\");
            }
            var reliability = new Dictionary<string, ManagementBaseObject>();
            try
            {
                foreach (var r in Query("SELECT DeviceId, Temperature, Wear, ReadErrorsTotal, WriteErrorsTotal, PowerOnHours FROM MSFT_StorageReliabilityCounter", @"root\Microsoft\Windows\Storage"))
                    reliability[o2s(r["DeviceId"])] = r;
            }
            catch { }
            foreach (var d in Query("SELECT DeviceId, FriendlyName, Size, MediaType, BusType, HealthStatus FROM MSFT_PhysicalDisk", @"root\Microsoft\Windows\Storage"))
            {
                var id = o2s(d["DeviceId"]);
                var disk = new DiskInfo
                {
                    Model = Str(d, "FriendlyName"), SizeGB = Math.Round(Long(d, "Size") / 1e9, 0),
                    MediaType = Int(d, "MediaType") switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "Unknown" },
                    BusType = Int(d, "BusType") switch { 17 => "NVMe", 11 => "SATA", 7 => "USB", 8 => "RAID", 10 => "SAS", 3 => "ATA", 12 => "SD", 13 => "MMC", 15 => "File-backed", 16 => "Storage Spaces", _ => "Other" },
                    Health = Int(d, "HealthStatus") switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => "Unknown" },
                    Volumes = letters.TryGetValue(id, out var v) ? v : new(),
                };
                if (reliability.TryGetValue(id, out var rc))
                {
                    disk.TemperatureC = NullIfZero(rc["Temperature"]);
                    disk.WearPercent = rc["Wear"] == null ? null : Convert.ToDouble(rc["Wear"]);
                    disk.ReadErrors = rc["ReadErrorsTotal"] == null ? null : Convert.ToInt64(rc["ReadErrorsTotal"]);
                    disk.WriteErrors = rc["WriteErrorsTotal"] == null ? null : Convert.ToInt64(rc["WriteErrorsTotal"]);
                    disk.PowerOnHours = rc["PowerOnHours"] == null ? null : Convert.ToInt64(rc["PowerOnHours"]);
                }
                foreach (var vol in disk.Volumes)
                {
                    try { var di = new DriveInfo(vol); disk.FreePercent = Math.Round(100.0 * di.AvailableFreeSpace / di.TotalSize, 1); } catch { }
                }
                inv.Disks.Add(disk);
            }
            try
            {
                bool predict = Query("SELECT PredictFailure FROM MSStorageDriver_FailurePredictStatus", @"root\WMI").Any(o => o["PredictFailure"] is bool b && b);
                if (predict) { inv.Notes.Add("A drive reports SMART 'predict failure'."); foreach (var d in inv.Disks) d.SmartPredictFailure ??= null; }
            }
            catch { }
        });
        Try(inv, "Board", () =>
        {
            foreach (var o in Query("SELECT Manufacturer, Product FROM Win32_BaseBoard")) inv.Motherboard = $"{Str(o, "Manufacturer")} {Str(o, "Product")}".Trim();
            foreach (var o in Query("SELECT SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS")) { inv.BiosVersion = Str(o, "SMBIOSBIOSVersion"); inv.BiosDate = CimDate(Str(o, "ReleaseDate")); }
            foreach (var o in Query("SELECT Caption, Version, LastBootUpTime FROM Win32_OperatingSystem")) { inv.OsName = Str(o, "Caption"); inv.OsVersion = Str(o, "Version"); inv.LastBoot = CimDate(Str(o, "LastBootUpTime")); }
        });
        Try(inv, "Power plan", () =>
        {
            var psi = new ProcessStartInfo("powercfg", "/getactivescheme") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            int a = output.LastIndexOf('('), b = output.LastIndexOf(')');
            inv.PowerPlan = a >= 0 && b > a ? output[(a + 1)..b] : output.Trim();
        });
        Try(inv, "Laptop / battery", () =>
        {
            int[] portable = { 8, 9, 10, 11, 12, 14, 18, 21, 30, 31, 32 };
            foreach (var o in Query("SELECT ChassisTypes FROM Win32_SystemEnclosure"))
                if (o["ChassisTypes"] is ushort[] types && types.Length > 0)
                {
                    inv.ChassisType = types[0] switch
                    {
                        3 => "Desktop", 4 => "Low-profile desktop", 6 => "Mini tower", 7 => "Tower", 8 => "Portable", 9 => "Laptop", 10 => "Notebook",
                        13 => "All-in-one", 14 => "Sub-notebook", 30 => "Tablet", 31 => "Convertible", 32 => "Detachable", 35 => "Mini PC", _ => $"Type {types[0]}",
                    };
                    if (types.Any(t => portable.Contains(t))) inv.IsLaptop = true;
                }
            var (onAc, hasBattery, _) = Core.Platform.PowerStatus.Get();
            inv.HasBattery = hasBattery || Query("SELECT Name FROM Win32_Battery").Count > 0;
            if (inv.HasBattery && inv.ChassisType is not ("Desktop" or "Tower" or "Mini tower" or "All-in-one" or "Low-profile desktop")) inv.IsLaptop = true;
            inv.OnBattery = onAc is bool a ? !a : null;
            if (inv.HasBattery)
            {
                try
                {
                    double design = Query("SELECT DesignedCapacity FROM BatteryStaticData", @"root\WMI").Select(o => Convert.ToDouble(o["DesignedCapacity"])).FirstOrDefault();
                    double full = Query("SELECT FullChargedCapacity FROM BatteryFullChargedCapacity", @"root\WMI").Select(o => Convert.ToDouble(o["FullChargedCapacity"])).FirstOrDefault();
                    if (design > 0 && full > 0)
                    {
                        inv.BatteryDesignCapacityWh = Math.Round(design / 1000, 1);
                        inv.BatteryFullChargeWh = Math.Round(full / 1000, 1);
                        inv.BatteryWearPercent = Math.Round(Math.Clamp(100 - 100 * full / design, 0, 100), 1);
                    }
                }
                catch { }
            }
        });
        Try(inv, "Windows settings", () =>
        {
            using var cc = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\CrashControl");
            if (cc?.GetValue("CrashDumpEnabled") is int dump) inv.CrashDumpSetting = dump;
            using var pw = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Power");
            if (pw?.GetValue("HiberbootEnabled") is int hb) inv.FastStartupEnabled = hb != 0;
            using var gd = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers");
            if (gd?.GetValue("TdrDelay") is int tdr) inv.TdrDelaySeconds = tdr;
        });
        return inv;
    }

    private static Dictionary<string, long> GpuMemoryFromRegistry()
    {
        var d = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls == null) return d;
            foreach (var sub in cls.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
            {
                using var k = cls.OpenSubKey(sub);
                var desc = k?.GetValue("DriverDesc") as string;
                var mem = k?.GetValue("HardwareInformation.qwMemorySize");
                if (desc == null || mem == null) continue;
                d[desc] = mem switch { long l => l, byte[] bytes when bytes.Length >= 8 => BitConverter.ToInt64(bytes, 0), _ => Convert.ToInt64(mem) };
            }
        }
        catch { }
        return d;
    }

    private static void Try(HardwareInventory inv, string what, Action a)
    {
        try { a(); }
        catch (Exception ex) { inv.Notes.Add($"Could not read {what}: {ex.Message}"); }
    }

    private static List<ManagementBaseObject> Query(string wql, string scope = @"root\cimv2")
    {
        using var s = new ManagementObjectSearcher(scope, wql);
        return s.Get().Cast<ManagementBaseObject>().ToList();
    }

    private static string o2s(object? o) => o?.ToString() ?? "";
    private static string Str(ManagementBaseObject o, string p) { try { return o[p]?.ToString() ?? ""; } catch { return ""; } }
    private static int Int(ManagementBaseObject o, string p) { try { return o[p] == null ? 0 : Convert.ToInt32(o[p], CultureInfo.InvariantCulture); } catch { return 0; } }
    private static long Long(ManagementBaseObject o, string p) { try { return o[p] == null ? 0 : Convert.ToInt64(o[p], CultureInfo.InvariantCulture); } catch { return 0; } }
    private static double? NullIfZero(object? o) { if (o == null) return null; var v = Convert.ToDouble(o); return v == 0 ? null : v; }

    private static DateTime? CimDate(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        try { return ManagementDateTimeConverter.ToDateTime(s); } catch { return null; }
    }
}
