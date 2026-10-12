using System.Buffers.Binary;
using BenchForge.Core.Model;

namespace BenchForge.Core.Storage;

/// <summary>Parses raw SMART data (SATA) and the NVMe health log page.</summary>
public static class SmartParser
{
    public static readonly IReadOnlyDictionary<int, string> AtaNames = new Dictionary<int, string>
    {
        [1] = "Read error rate", [3] = "Spin-up time", [4] = "Start/stop count", [5] = "Reallocated sectors", [7] = "Seek error rate",
        [9] = "Power-on hours", [10] = "Spin retry count", [12] = "Power cycles", [169] = "Remaining life", [170] = "Available reserved space",
        [171] = "Program fail count", [172] = "Erase fail count", [173] = "Wear levelling count", [174] = "Unexpected power loss",
        [177] = "Wear levelling count", [179] = "Used reserved blocks", [181] = "Program fail count", [182] = "Erase fail count",
        [183] = "SATA downshift errors", [184] = "End-to-end errors", [187] = "Reported uncorrectable errors", [188] = "Command timeouts",
        [190] = "Airflow temperature", [192] = "Power-off retract count", [194] = "Temperature", [195] = "Hardware ECC recovered",
        [196] = "Reallocation events", [197] = "Pending sectors", [198] = "Uncorrectable sectors", [199] = "Interface CRC errors (cable)",
        [200] = "Write error rate", [202] = "Percent lifetime used", [231] = "SSD life left", [233] = "Media wearout indicator",
        [241] = "Total LBAs written", [242] = "Total LBAs read",
    };

    /// <summary>
    /// Parses MSStorageDriver_ATAPISmartData.VendorSpecific (and optionally MSStorageDriver_FailurePredictThresholds.VendorSpecific):
    /// 2-byte header then 30 entries of 12 bytes (id, flags[2], current, worst, raw[6], reserved).
    /// </summary>
    public static List<SmartAttribute> ParseAta(byte[] data, byte[]? thresholds = null)
    {
        var th = new Dictionary<int, int>();
        if (thresholds != null)
            for (int i = 0; i < 30 && 2 + i * 12 + 1 < thresholds.Length; i++)
            {
                int id = thresholds[2 + i * 12];
                if (id != 0) th[id] = thresholds[2 + i * 12 + 1];
            }
        var list = new List<SmartAttribute>();
        for (int i = 0; i < 30 && 2 + i * 12 + 11 < data.Length; i++)
        {
            int o = 2 + i * 12;
            int id = data[o];
            if (id == 0) continue;
            long raw = 0;
            for (int b = 5; b >= 0; b--) raw = (raw << 8) | data[o + 5 + b];
            // Temperature attributes keep the current value in the lowest byte.
            if (id is 190 or 194) raw &= 0xFF;
            list.Add(new SmartAttribute(id, AtaNames.TryGetValue(id, out var n) ? n : $"Attribute {id}", data[o + 3], data[o + 4], th.TryGetValue(id, out var t) ? t : null, raw));
        }
        return list;
    }

    /// <summary>Parses the 512-byte NVMe SMART / health information log (log page 0x02).</summary>
    public static NvmeHealthInfo ParseNvme(ReadOnlySpan<byte> log)
    {
        if (log.Length < 192) throw new ArgumentException("NVMe health log must be 512 bytes");
        static double U128Low(ReadOnlySpan<byte> s) => BinaryPrimitives.ReadUInt64LittleEndian(s) + BinaryPrimitives.ReadUInt64LittleEndian(s[8..]) * 18446744073709551616.0;
        const double DataUnit = 512_000; // bytes per NVMe "data unit"
        return new NvmeHealthInfo
        {
            CriticalWarning = log[0],
            TemperatureC = BinaryPrimitives.ReadUInt16LittleEndian(log[1..]) - 273,
            AvailableSparePercent = log[3],
            SpareThresholdPercent = log[4],
            PercentageUsed = log[5],
            DataReadTB = Math.Round(U128Low(log[32..]) * DataUnit / 1e12, 2),
            DataWrittenTB = Math.Round(U128Low(log[48..]) * DataUnit / 1e12, 2),
            PowerCycles = (long)U128Low(log[112..]),
            PowerOnHours = (long)U128Low(log[128..]),
            UnsafeShutdowns = (long)U128Low(log[144..]),
            MediaErrors = (long)U128Low(log[160..]),
            ErrorLogEntries = (long)U128Low(log[176..]),
        };
    }

    public static string DescribeCriticalWarning(int flags)
    {
        var l = new List<string>();
        if ((flags & 1) != 0) l.Add("spare space below threshold");
        if ((flags & 2) != 0) l.Add("temperature out of range");
        if ((flags & 4) != 0) l.Add("reliability degraded (internal errors)");
        if ((flags & 8) != 0) l.Add("drive switched to read-only");
        if ((flags & 16) != 0) l.Add("backup memory failed");
        return l.Count == 0 ? "none" : string.Join(", ", l);
    }

    /// <summary>Typical sequential read speed (MB/s) a healthy drive of this kind should reach.</summary>
    public static (double ExpectedSeqRead, string Kind) Expected(DiskInfo d, PcieLink? link)
    {
        if (d.BusType.Contains("NVMe", StringComparison.OrdinalIgnoreCase))
        {
            int gen = link?.MaxGen ?? 0;
            return gen switch
            {
                >= 5 => (7000, "PCIe 5.0 NVMe SSD"),
                4 => (3000, "PCIe 4.0 NVMe SSD"),
                3 => (1700, "PCIe 3.0 NVMe SSD"),
                _ => (1500, "NVMe SSD"),
            };
        }
        if (d.BusType.Contains("USB", StringComparison.OrdinalIgnoreCase)) return (0, "USB drive");
        if (d.MediaType == "SSD") return (420, "SATA SSD");
        if (d.MediaType == "HDD") return (90, "hard drive");
        return (0, d.MediaType);
    }
}
