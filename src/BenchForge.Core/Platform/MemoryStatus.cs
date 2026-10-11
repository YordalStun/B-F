using System.Runtime.InteropServices;

namespace BenchForge.Core.Platform;

public static partial class MemoryStatus
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    /// <summary>(total, available) physical memory in bytes.</summary>
    public static (ulong Total, ulong Available) Get()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                if (GlobalMemoryStatusEx(ref m)) return (m.ullTotalPhys, m.ullAvailPhys);
            }
            else if (File.Exists("/proc/meminfo"))
            {
                ulong total = 0, avail = 0;
                foreach (var line in File.ReadLines("/proc/meminfo"))
                {
                    var parts = line.Split(':', StringSplitOptions.TrimEntries);
                    if (parts.Length < 2) continue;
                    var kb = ulong.Parse(parts[1].Split(' ')[0]) * 1024;
                    if (parts[0] == "MemTotal") total = kb;
                    else if (parts[0] == "MemAvailable") avail = kb;
                }
                return (total, avail);
            }
        }
        catch { }
        var gc = GC.GetGCMemoryInfo();
        return ((ulong)gc.TotalAvailableMemoryBytes, (ulong)(gc.TotalAvailableMemoryBytes / 2));
    }
}
