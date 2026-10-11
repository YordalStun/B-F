using System.Runtime.InteropServices;

namespace BenchForge.Core.Platform;

/// <summary>Charger / battery state (Windows GetSystemPowerStatus).</summary>
public static partial class PowerStatus
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    /// <summary>(on charger?, has battery?, battery %) — nulls when unknown.</summary>
    public static (bool? OnAc, bool HasBattery, int? BatteryPercent) Get()
    {
        try
        {
            if (OperatingSystem.IsWindows() && GetSystemPowerStatus(out var s))
            {
                bool hasBattery = (s.BatteryFlag & 128) == 0 && s.BatteryFlag != 255;
                bool? ac = s.ACLineStatus switch { 0 => false, 1 => true, _ => null };
                return (ac, hasBattery, s.BatteryLifePercent <= 100 ? s.BatteryLifePercent : null);
            }
        }
        catch { }
        return (null, false, null);
    }
}
