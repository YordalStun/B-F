using BenchForge.Core.Model;

namespace BenchForge.Core.Analysis;

/// <summary>
/// Recognises "Windows ran out of memory" (RAM + page file full). That stops a test from running at all;
/// it is not a hardware error, so it must never count as "the RAM returned wrong data".
/// </summary>
public static class MemoryShortage
{
    public const string Prefix = "Not enough free memory";

    private static readonly string[] Signs =
    {
        Prefix, "Insufficient memory", "OutOfMemory", "out of memory", "Not enough memory",
        "paging file is too small", "0x800705AF", "0x8007000E", "0x800705AA",
    };

    /// <summary>Set by the app: reads the current memory state (null where it can't be read).</summary>
    public static Func<MemorySnapshot?>? Probe { get; set; }

    public static bool Is(string? text) => text != null && Signs.Any(s => text.Contains(s, StringComparison.OrdinalIgnoreCase));

    public static bool Is(Exception ex) => ex is OutOfMemoryException or InsufficientMemoryException || Is(ex.Message) || (ex.InnerException != null && Is(ex.InnerException));

    public static bool Is(ModuleResult m) => m.Failed && Is(m.FailureReason);

    public static string Describe(string detail) =>
        Is(detail) && detail.StartsWith(Prefix, StringComparison.Ordinal) ? detail :
        $"{Prefix}: Windows ran out of memory (RAM + page file are full), so this test could not run. This is not a hardware fault. ({detail})";

    public static MemorySnapshot? TryProbe()
    {
        try { return Probe?.Invoke(); } catch { return null; }
    }

    /// <summary>Why memory is too low to test reliably, or null when it is fine.</summary>
    public static string? Problem(MemorySnapshot? m)
    {
        if (m == null) return null;
        var parts = new List<string>();
        if (m.CommitLimitGB > 0 && m.CommittedGB / m.CommitLimitGB > 0.9)
            parts.Add($"Windows has handed out {m.CommittedGB:0.0} GB of its {m.CommitLimitGB:0.0} GB memory limit (RAM + page file, {m.CommittedGB / m.CommitLimitGB:P0})");
        if (m.TotalGB > 0 && m.AvailableGB < Math.Min(1.0, m.TotalGB * 0.1))
            parts.Add($"only {m.AvailableGB:0.0} GB of {m.TotalGB:0.0} GB RAM is free");
        return parts.Count == 0 ? null : string.Join("; ", parts) + ".";
    }

    public static string Advice(MemorySnapshot? m) =>
        "Restart the PC (Start › Restart — 'Shut down' does not reset Windows while Fast Startup is on), close big programs, then test again." +
        (m?.TopProcesses.Count > 0 ? " Biggest memory users right now: " + string.Join(", ", m.TopProcesses) + "." : "");
}
