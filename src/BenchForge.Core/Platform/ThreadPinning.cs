using System.Runtime.InteropServices;

namespace BenchForge.Core.Platform;

/// <summary>Pins the calling thread to one logical processor so errors can be traced to a specific core.</summary>
public static partial class ThreadPinning
{
    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentThread();

    [LibraryImport("kernel32.dll")]
    private static partial nuint SetThreadAffinityMask(nint hThread, nuint mask);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int sched_setaffinity(int pid, nint cpusetsize, ref ulong mask);

    public static bool Pin(int logicalCpu)
    {
        if (logicalCpu < 0 || logicalCpu >= 64) return false;
        try
        {
            if (OperatingSystem.IsWindows())
                return SetThreadAffinityMask(GetCurrentThread(), (nuint)1 << logicalCpu) != 0;
            if (OperatingSystem.IsLinux())
            {
                ulong mask = 1UL << logicalCpu;
                return sched_setaffinity(0, sizeof(ulong), ref mask) == 0;
            }
        }
        catch { }
        return false;
    }
}
