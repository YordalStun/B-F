using System.Management;
using BenchForge.Core.Events;
using BenchForge.Core.Model;
using BenchForge.Core.Persistence;

namespace BenchForge.App.Platform;

/// <summary>Gathers the slow extras for "Export for AI": drivers, Reliability Monitor history, recent error events, app log.</summary>
public static class DiagnosticsCollector
{
    private static readonly string[] InterestingClasses = { "DISPLAY", "NET", "SCSIADAPTER", "HDC", "MEDIA", "USB", "SYSTEM", "BLUETOOTH", "MONITOR", "PROCESSOR", "DISKDRIVE" };

    public static DiagnosticsExtras Collect(IEventSource events)
    {
        var x = new DiagnosticsExtras();
        try
        {
            using var s = new ManagementObjectSearcher("SELECT DeviceClass, DeviceName, DriverProviderName, DriverVersion, DriverDate FROM Win32_PnPSignedDriver");
            foreach (ManagementObject o in s.Get())
            {
                var cls = o["DeviceClass"]?.ToString() ?? "";
                var provider = o["DriverProviderName"]?.ToString() ?? "";
                var name = o["DeviceName"]?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(name) || !InterestingClasses.Contains(cls.ToUpperInvariant())) continue;
                // Generic in-box Microsoft system devices are noise; keep Microsoft drivers for display/storage/net.
                if (cls.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase) && provider.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)) continue;
                DateTime? date = null;
                try { if (o["DriverDate"] is string d && d.Length > 0) date = ManagementDateTimeConverter.ToDateTime(d); } catch { }
                x.Drivers.Add(new DriverEntry(cls, name, provider, o["DriverVersion"]?.ToString() ?? "", date));
            }
            x.Drivers = x.Drivers.DistinctBy(d => (d.Device, d.Version)).Take(250).ToList();
        }
        catch (Exception ex) { x.Notes.Add("Driver list unavailable: " + ex.Message); }

        try
        {
            var since = ManagementDateTimeConverter.ToDmtfDateTime(DateTime.Now.AddDays(-30));
            using var s = new ManagementObjectSearcher($"SELECT TimeGenerated, SourceName, ProductName, Message, EventIdentifier FROM Win32_ReliabilityRecords WHERE TimeGenerated > '{since}'");
            s.Options.Timeout = TimeSpan.FromSeconds(30);
            foreach (ManagementObject o in s.Get())
            {
                DateTime t = DateTime.MinValue;
                try { t = ManagementDateTimeConverter.ToDateTime(o["TimeGenerated"]?.ToString() ?? ""); } catch { }
                x.Reliability.Add(new ReliabilityEntry(t, o["SourceName"]?.ToString() ?? "", o["ProductName"]?.ToString() ?? "", o["Message"]?.ToString() ?? "", Convert.ToInt32(o["EventIdentifier"] ?? 0)));
                if (x.Reliability.Count >= 400) break;
            }
        }
        catch (Exception ex) { x.Notes.Add("Reliability history unavailable: " + ex.Message); }

        try
        {
            var spec = new EventQuerySpec { Since = DateTime.Now.AddDays(-7), Levels = { 1, 2 }, Logs = { "System", "Application" } };
            x.RecentErrors = events.Query(spec, 400);
        }
        catch (Exception ex) { x.Notes.Add("Recent errors unavailable: " + ex.Message); }

        try
        {
            if (File.Exists(DataPaths.AppLog))
            {
                using var fs = new FileStream(DataPaths.AppLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var r = new StreamReader(fs);
                x.AppLogTail = r.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).TakeLast(200).ToList();
            }
        }
        catch { }
        return x;
    }
}
