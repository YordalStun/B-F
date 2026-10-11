namespace BenchForge.Core.Model;

/// <summary>Extra system information gathered only for exports (slow to collect).</summary>
public sealed class DiagnosticsExtras
{
    public List<DriverEntry> Drivers { get; set; } = new();
    public List<ReliabilityEntry> Reliability { get; set; } = new();
    /// <summary>All error/critical events from System + Application in the last days.</summary>
    public List<EventEntry> RecentErrors { get; set; } = new();
    public List<string> AppLogTail { get; set; } = new();
    public List<string> Notes { get; set; } = new();
}

public sealed record DriverEntry(string DeviceClass, string Device, string Provider, string Version, DateTime? Date);

/// <summary>A Windows Reliability Monitor record (app crashes, hardware errors, failed updates, …).</summary>
public sealed record ReliabilityEntry(DateTime Time, string Source, string Product, string Message, int EventId);
