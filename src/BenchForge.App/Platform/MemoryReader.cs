using System.Diagnostics;
using System.Runtime.InteropServices;
using BenchForge.Core.Model;

namespace BenchForge.App.Platform;

/// <summary>Free RAM, the commit charge (RAM + page file) and the processes using the most memory.</summary>
public static class MemoryReader
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    public static MemorySnapshot? Read()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref m)) return null;
        const double GB = 1L << 30;
        // ullTotalPageFile / ullAvailPageFile are the commit limit / what is still free of it (RAM + page file).
        var snap = new MemorySnapshot
        {
            TotalGB = m.ullTotalPhys / GB, AvailableGB = m.ullAvailPhys / GB,
            CommitLimitGB = m.ullTotalPageFile / GB, CommittedGB = (m.ullTotalPageFile - m.ullAvailPageFile) / GB,
        };
        try
        {
            var top = new List<(string Name, long Bytes)>();
            foreach (var p in Process.GetProcesses())
                using (p)
                    try { top.Add((p.ProcessName, p.PrivateMemorySize64)); } catch { /* exited or protected */ }
            snap.TopProcesses = top.GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Name: g.Key, Bytes: g.Sum(x => x.Bytes), Count: g.Count()))
                .OrderByDescending(x => x.Bytes).Take(5)
                .Select(x => $"{x.Name}{(x.Count > 1 ? $" ×{x.Count}" : "")} {x.Bytes / GB:0.0} GB").ToList();
        }
        catch { /* the list is only a hint */ }
        return snap;
    }
}
