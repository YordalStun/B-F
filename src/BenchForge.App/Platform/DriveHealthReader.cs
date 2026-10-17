using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using BenchForge.Core.Model;
using BenchForge.Core.Storage;
using Microsoft.Win32.SafeHandles;

namespace BenchForge.App.Platform;

/// <summary>Reads drive health straight from the hardware: SATA SMART, NVMe health log, PCIe links, TRIM, partition alignment.</summary>
public static partial class DriveHealthReader
{
    public static void Fill(HardwareInventory inv)
    {
        try { Smart(inv); } catch (Exception ex) { inv.Notes.Add("SMART: " + ex.Message); }
        try { foreach (var d in inv.Disks.Where(d => d.BusType == "NVMe" && d.DiskNumber is int)) d.Nvme = ReadNvme(d.DiskNumber!.Value); }
        catch (Exception ex) { inv.Notes.Add("NVMe health: " + ex.Message); }
        try { PcieLinks(inv); } catch (Exception ex) { inv.Notes.Add("PCIe link: " + ex.Message); }
        try { Alignment(inv); } catch { }
        try { inv.TrimEnabled = ReadTrim(); } catch { }
    }

    // ---------------------------------------------------------------- SATA SMART (WMI)

    private static void Smart(HardwareInventory inv)
    {
        // Win32_DiskDrive gives disk index ↔ PNP id; the SMART WMI classes are keyed by PNP id + "_0".
        var pnpToIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using (var s = new ManagementObjectSearcher("SELECT Index, PNPDeviceID FROM Win32_DiskDrive"))
            foreach (ManagementObject o in s.Get())
                if (o["PNPDeviceID"] is string pnp) pnpToIndex[pnp] = Convert.ToInt32(o["Index"]);
        var thresholds = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        using (var s = new ManagementObjectSearcher(@"root\WMI", "SELECT InstanceName, VendorSpecific FROM MSStorageDriver_FailurePredictThresholds"))
            foreach (ManagementObject o in s.Get())
                if (o["InstanceName"] is string n && o["VendorSpecific"] is byte[] b) thresholds[n] = b;
        using (var s = new ManagementObjectSearcher(@"root\WMI", "SELECT InstanceName, VendorSpecific FROM MSStorageDriver_ATAPISmartData"))
            foreach (ManagementObject o in s.Get())
            {
                if (o["InstanceName"] is not string name || o["VendorSpecific"] is not byte[] data) continue;
                var pnp = name.EndsWith("_0", StringComparison.Ordinal) ? name[..^2] : name;
                if (!pnpToIndex.TryGetValue(pnp, out int idx)) continue;
                var disk = inv.Disks.FirstOrDefault(d => d.DiskNumber == idx);
                if (disk != null) disk.Smart = SmartParser.ParseAta(data, thresholds.GetValueOrDefault(name));
            }
    }

    // ---------------------------------------------------------------- NVMe health log (IOCTL)

    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inBuf, uint inSize, byte[] outBuf, uint outSize, out uint returned, nint overlapped);

    public static NvmeHealthInfo? ReadNvme(int disk)
    {
        using var h = File.OpenHandle($@"\\.\PhysicalDrive{disk}", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        // STORAGE_PROPERTY_QUERY { PropertyId = StorageDeviceProtocolSpecificProperty (50), QueryType = PropertyStandardQuery (0) }
        // + STORAGE_PROTOCOL_SPECIFIC_DATA { ProtocolTypeNvme (3), NVMeDataTypeLogPage (2), log 0x02, sub 0, offset 40, length 512 }
        var buf = new byte[8 + 40 + 512];
        BitConverter.TryWriteBytes(buf.AsSpan(0), 50);
        BitConverter.TryWriteBytes(buf.AsSpan(4), 0);
        BitConverter.TryWriteBytes(buf.AsSpan(8), 3);
        BitConverter.TryWriteBytes(buf.AsSpan(12), 2);
        BitConverter.TryWriteBytes(buf.AsSpan(16), 2);
        BitConverter.TryWriteBytes(buf.AsSpan(20), 0);
        BitConverter.TryWriteBytes(buf.AsSpan(24), 40);
        BitConverter.TryWriteBytes(buf.AsSpan(28), 512);
        if (!DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, buf, (uint)buf.Length, buf, (uint)buf.Length, out uint ret, 0) || ret < 8 + 40)
            return null;
        // Output: STORAGE_PROTOCOL_DATA_DESCRIPTOR { Version, Size, STORAGE_PROTOCOL_SPECIFIC_DATA } — data offset is relative to that struct (byte 8).
        int offset = BitConverter.ToInt32(buf, 8 + 16);
        int length = BitConverter.ToInt32(buf, 8 + 20);
        if (length < 192 || 8 + offset + 512 > buf.Length) return null;
        return SmartParser.ParseNvme(buf.AsSpan(8 + offset, 512));
    }

    // ---------------------------------------------------------------- PCIe link speed / width

    private static readonly string[] LinkKeys =
    {
        "DEVPKEY_PciDevice_CurrentLinkSpeed", "DEVPKEY_PciDevice_CurrentLinkWidth", "DEVPKEY_PciDevice_MaxLinkSpeed", "DEVPKEY_PciDevice_MaxLinkWidth",
    };

    private static void PcieLinks(HardwareInventory inv)
    {
        using var s = new ManagementObjectSearcher("SELECT * FROM Win32_PnPEntity WHERE PNPClass = 'SCSIAdapter' OR PNPClass = 'Display'");
        foreach (ManagementObject dev in s.Get())
        {
            var name = dev["Name"]?.ToString() ?? "";
            var cls = dev["PNPClass"]?.ToString() ?? "";
            var id = dev["DeviceID"]?.ToString() ?? "";
            if (!id.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)) continue;
            bool nvme = cls == "SCSIAdapter" && (name.Contains("NVM", StringComparison.OrdinalIgnoreCase) || name.Contains("NVMe", StringComparison.OrdinalIgnoreCase));
            if (cls == "SCSIAdapter" && !nvme) continue;
            var values = new Dictionary<string, int>();
            try
            {
                var inParams = dev.GetMethodParameters("GetDeviceProperties");
                inParams["devicePropertyKeys"] = LinkKeys;
                var outParams = dev.InvokeMethod("GetDeviceProperties", inParams, null);
                if (outParams?["deviceProperties"] is ManagementBaseObject[] props)
                    foreach (var p in props)
                        if (p["KeyName"] is string k && p["Data"] != null && int.TryParse(p["Data"].ToString(), out int v)) values[k] = v;
            }
            catch { continue; }
            if (values.Count < 4) continue;
            inv.PcieLinks.Add(new PcieLink(name, nvme ? "NVMe" : "GPU",
                values[LinkKeys[0]], values[LinkKeys[1]], values[LinkKeys[2]], values[LinkKeys[3]]));
        }
    }

    // ---------------------------------------------------------------- TRIM + alignment

    private static bool? ReadTrim()
    {
        var psi = new ProcessStartInfo("fsutil", "behavior query DisableDeleteNotify") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(5000);
        foreach (var line in output.Split('\n'))
            if (line.Contains("NTFS", StringComparison.OrdinalIgnoreCase) && line.Contains('='))
            {
                var v = line.Split('=')[1].Trim();
                if (v.StartsWith("0")) return true;
                if (v.StartsWith("1")) return false;
            }
        return null;
    }

    private static void Alignment(HardwareInventory inv)
    {
        // Same source as the alignment window (MSFT_Partition), so the dashboard and the window always agree.
        // Win32_DiskPartition is only the fallback: it can disagree with what Windows' storage stack reports.
        try { AlignmentFromStorage(inv); return; }
        catch { foreach (var d in inv.Disks) { d.Misaligned = null; d.MisalignedPartitions.Clear(); } }
        AlignmentFromWin32(inv);
    }

    private static void AlignmentFromStorage(HardwareInventory inv)
    {
        using var s = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
            "SELECT DiskNumber, PartitionNumber, DriveLetter, Offset, MbrType FROM MSFT_Partition");
        int seen = 0;
        foreach (ManagementObject o in s.Get())
        {
            seen++;
            var d = inv.Disks.FirstOrDefault(x => x.DiskNumber == Convert.ToInt32(o["DiskNumber"]));
            if (d == null) continue;
            // An MBR extended container only holds other partitions; its own start doesn't matter.
            if (o["MbrType"] != null && Convert.ToInt32(o["MbrType"]) is 5 or 15) continue;
            long off = Convert.ToInt64(o["Offset"]);
            if (off % 4096 != 0)
            {
                d.Misaligned = true;
                char letter = o["DriveLetter"] is char c ? c : Convert.ToChar(Convert.ToUInt16(o["DriveLetter"] ?? 0));
                int num = Convert.ToInt32(o["PartitionNumber"]);
                var label = char.IsLetter(letter) ? $"{char.ToUpperInvariant(letter)}: (partition {num})" : $"Partition {num}";
                d.MisalignedPartitions.Add($"{label} starts at {off:N0} bytes — not a multiple of 4,096");
            }
            else d.Misaligned ??= false;
        }
        if (seen == 0) throw new InvalidOperationException("no partitions");
    }

    private static void AlignmentFromWin32(HardwareInventory inv)
    {
        // Partition DeviceID ("Disk #0, Partition #1") → drive letter ("C:").
        var letters = new Dictionary<string, string>();
        try
        {
            using var map = new ManagementObjectSearcher("SELECT Antecedent, Dependent FROM Win32_LogicalDiskToPartition");
            foreach (ManagementObject m in map.Get())
            {
                string? part = Quoted(m["Antecedent"]?.ToString()), letter = Quoted(m["Dependent"]?.ToString());
                if (part != null && letter != null) letters[part] = letters.TryGetValue(part, out var l) ? l + ", " + letter : letter;
            }
        }
        catch { /* letters are only a nicer label */ }

        using var s = new ManagementObjectSearcher("SELECT DeviceID, DiskIndex, Index, StartingOffset FROM Win32_DiskPartition");
        foreach (ManagementObject o in s.Get())
        {
            int idx = Convert.ToInt32(o["DiskIndex"]);
            long off = Convert.ToInt64(o["StartingOffset"]);
            var d = inv.Disks.FirstOrDefault(x => x.DiskNumber == idx);
            if (d == null) continue;
            if (off % 4096 != 0)
            {
                d.Misaligned = true;
                // Win32_DiskPartition.Index is 0-based; Disk Management shows partitions from 1.
                int num = Convert.ToInt32(o["Index"]) + 1;
                var label = letters.TryGetValue(o["DeviceID"]?.ToString() ?? "", out var l) ? $"{l} (partition {num})" : $"Partition {num}";
                d.MisalignedPartitions.Add($"{label} starts at {off:N0} bytes — not a multiple of 4,096");
            }
            else d.Misaligned ??= false;
        }
    }

    /// <summary>Pulls the value out of a WMI reference like <c>Win32_DiskPartition.DeviceID="Disk #0, Partition #1"</c>.</summary>
    private static string? Quoted(string? reference)
    {
        if (reference == null) return null;
        int a = reference.IndexOf('"'), b = reference.LastIndexOf('"');
        return a >= 0 && b > a ? reference[(a + 1)..b] : null;
    }
}
