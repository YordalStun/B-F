using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using BenchForge.Core.Model;

namespace BenchForge.Core.Events;

/// <summary>Selects events from one log, optionally by provider and event IDs.</summary>
public sealed record EventSelector(string Log, string? Provider = null, params int[] Ids);

public sealed class EventQuerySpec
{
    public List<EventSelector> Selectors { get; set; } = new();
    /// <summary>Logs searched when there are no selectors.</summary>
    public List<string> Logs { get; set; } = new() { "System", "Application" };
    /// <summary>1 critical, 2 error, 3 warning, 4 information. Empty = all.</summary>
    public List<int> Levels { get; set; } = new();
    public DateTime? Since { get; set; }
    public DateTime? Until { get; set; }
    /// <summary>Case-insensitive text that must appear in the message/provider (applied after reading).</summary>
    public string? Text { get; set; }
    public string Description { get; set; } = "";

    public IEnumerable<string> AllLogs => Selectors.Count > 0 ? Selectors.Select(s => s.Log).Distinct(StringComparer.OrdinalIgnoreCase) : Logs;

    public bool MatchesText(EventEntry e) =>
        string.IsNullOrWhiteSpace(Text) ||
        e.Message.Contains(Text, StringComparison.OrdinalIgnoreCase) ||
        e.Provider.Contains(Text, StringComparison.OrdinalIgnoreCase) ||
        e.Id.ToString() == Text.Trim();

    /// <summary>Builds the event log XPath filter for one log (the subset of XPath Windows supports).</summary>
    public string BuildXPath(string log)
    {
        var parts = new List<string>();
        var sels = Selectors.Where(s => string.Equals(s.Log, log, StringComparison.OrdinalIgnoreCase)).ToList();
        if (sels.Count > 0)
        {
            var ors = sels.Select(s =>
            {
                var conds = new List<string>();
                if (!string.IsNullOrEmpty(s.Provider)) conds.Add($"Provider[@Name='{Escape(s.Provider)}']");
                if (s.Ids.Length > 0) conds.Add("(" + string.Join(" or ", s.Ids.Select(i => $"EventID={i}")) + ")");
                return conds.Count == 0 ? "true()" : "(" + string.Join(" and ", conds) + ")";
            }).ToList();
            if (!ors.Contains("true()")) parts.Add("(" + string.Join(" or ", ors) + ")");
        }
        if (Levels.Count > 0)
        {
            var lv = Levels.SelectMany(l => l == 4 ? new[] { 4, 0 } : new[] { l }).Distinct();
            parts.Add("(" + string.Join(" or ", lv.Select(l => $"Level={l}")) + ")");
        }
        var time = new List<string>();
        if (Since is DateTime s1) time.Add($"@SystemTime>='{s1.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)}'");
        if (Until is DateTime u1) time.Add($"@SystemTime<='{u1.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)}'");
        if (time.Count > 0) parts.Add("TimeCreated[" + string.Join(" and ", time) + "]");
        return parts.Count == 0 ? "*" : "*[System[" + string.Join(" and ", parts) + "]]";
    }

    private static string Escape(string s) => s.Replace("'", "&apos;");
}

public sealed record EventPreset(string Key, string Title, string Description, EventSelector[] Selectors, int[]? Levels = null);

public static class EventPresets
{
    public const string Sys = "System", App = "Application";

    public static readonly EventPreset BlueScreens = new("bsod", "Blue screens", "Windows stop errors (BSOD) recorded after reboot.",
        new[] { new EventSelector(Sys, "Microsoft-Windows-WER-SystemErrorReporting", 1001), new EventSelector(Sys, "BugCheck", 1001), new EventSelector(App, "Windows Error Reporting", 1001) });

    public static readonly EventPreset UnexpectedShutdowns = new("shutdown", "Unexpected shutdowns", "Power loss, hard resets, freezes and crashes that rebooted the PC.",
        new[] { new EventSelector(Sys, "Microsoft-Windows-Kernel-Power", 41), new EventSelector(Sys, "EventLog", 6008) });

    public static readonly EventPreset Whea = new("whea", "Hardware errors (WHEA)", "Errors reported by the CPU, memory controller or PCIe bus — strong sign of instability or failing hardware.",
        new[] { new EventSelector(Sys, "Microsoft-Windows-WHEA-Logger") });

    public static readonly EventPreset GpuDriver = new("gpu", "GPU driver crashes", "Graphics driver stopped responding / reset (TDR) and GPU driver errors.",
        new[] { new EventSelector(Sys, "Display", 4101), new EventSelector(Sys, "nvlddmkm"), new EventSelector(Sys, "amdkmdag"), new EventSelector(Sys, "amdkmdap"), new EventSelector(Sys, "igfx"), new EventSelector(Sys, "igfxn") },
        new[] { 1, 2, 3 });

    public static readonly EventPreset AppCrashes = new("apps", "Application crashes", "Programs that crashed or hung.",
        new[] { new EventSelector(App, "Application Error", 1000), new EventSelector(App, "Application Hang", 1002), new EventSelector(App, ".NET Runtime", 1026) });

    public static readonly EventPreset Disk = new("disk", "Drive errors", "Bad blocks, controller resets, file system corruption.",
        new[] { new EventSelector(Sys, "disk", 7, 11, 15, 51, 52, 153, 154), new EventSelector(Sys, "Ntfs", 41, 55, 98, 130, 137, 140), new EventSelector(Sys, "Microsoft-Windows-Ntfs", 55, 98, 137, 140),
                new EventSelector(Sys, "stornvme", 11, 129), new EventSelector(Sys, "storahci", 129), new EventSelector(Sys, "iaStorA", 129), new EventSelector(Sys, "volmgr", 46, 161, 162) },
        new[] { 1, 2, 3 });

    public static readonly EventPreset Services = new("services", "Driver & service failures", "Drivers or services that failed to start or crashed.",
        new[] { new EventSelector(Sys, "Service Control Manager", 7000, 7001, 7009, 7011, 7022, 7023, 7024, 7026, 7031, 7034) });

    public static readonly EventPreset MemoryDiag = new("memdiag", "Windows Memory Diagnostic", "Results of the built-in Windows RAM test.",
        new[] { new EventSelector(Sys, "Microsoft-Windows-MemoryDiagnostics-Results") });

    public static readonly EventPreset Thermal = new("thermal", "Throttling & thermal", "CPU speed limited by firmware (heat/power) and thermal events.",
        new[] { new EventSelector(Sys, "Microsoft-Windows-Kernel-Processor-Power", 37), new EventSelector(Sys, "Microsoft-Windows-Kernel-Power", 86, 125) });

    public static readonly EventPreset Boot = new("boot", "Startups & shutdowns", "When Windows started, shut down or was restarted (and by what).",
        new[] { new EventSelector(Sys, "Microsoft-Windows-Kernel-General", 12, 13), new EventSelector(Sys, "User32", 1074), new EventSelector(Sys, "EventLog", 6005, 6006, 6008) });

    public static readonly EventPreset Errors = new("errors", "All errors & critical", "Every error and critical event in System and Application.",
        new[] { new EventSelector(Sys), new EventSelector(App) }, new[] { 1, 2 });

    public static readonly EventPreset[] All = { BlueScreens, UnexpectedShutdowns, Whea, GpuDriver, AppCrashes, Disk, Services, MemoryDiag, Thermal, Boot, Errors };

    /// <summary>Everything that can matter for a crash (used when correlating events with a crash time).</summary>
    public static EventQuerySpec CrashRelated(DateTime since, DateTime until)
    {
        var spec = new EventQuerySpec { Since = since, Until = until, Description = "Crash-related events" };
        foreach (var p in new[] { BlueScreens, UnexpectedShutdowns, Whea, GpuDriver, Disk, Thermal })
            spec.Selectors.AddRange(p.Selectors);
        spec.Selectors.Add(new EventSelector(App, "Application Error", 1000));
        spec.Selectors.Add(new EventSelector(Sys, "Service Control Manager", 7031, 7034));
        return spec;
    }

    public static EventQuerySpec ToSpec(this EventPreset p, DateTime? since = null) => new()
    {
        Selectors = p.Selectors.ToList(),
        Levels = p.Levels?.ToList() ?? new(),
        Since = since,
        Description = p.Title,
    };
}

/// <summary>
/// Turns plain requests like "show me all bluescreens from last week" or "gpu crashes today" into an event query.
/// Works fully offline.
/// </summary>
public static partial class EventQueryParser
{
    private static readonly (string[] Words, EventPreset Preset)[] Keywords =
    {
        (new[] { "blue screen", "bluescreen", "bsod", "bugcheck", "bug check", "stop code", "stop error", "blue" }, EventPresets.BlueScreens),
        (new[] { "whea", "hardware error", "machine check", "mce", "hardware fault" }, EventPresets.Whea),
        (new[] { "unexpected shutdown", "power loss", "lost power", "kernel-power", "kernel power", "hard reset", "froze", "freeze", "reboot", "restarted", "shut down", "shutdown", "turned off", "power off" }, EventPresets.UnexpectedShutdowns),
        (new[] { "gpu", "graphics", "display driver", "tdr", "nvidia", "amd driver", "radeon", "black screen", "video driver", "nvlddmkm" }, EventPresets.GpuDriver),
        (new[] { "app crash", "application crash", "program crash", "application error", "game crash", "crashed app", "hang", "not responding", "applications", "apps" }, EventPresets.AppCrashes),
        (new[] { "disk", "drive", "ssd", "hdd", "nvme", "ntfs", "storage", "bad block", "bad sector" }, EventPresets.Disk),
        (new[] { "service", "driver fail", "failed to start", "driver error" }, EventPresets.Services),
        (new[] { "memory diagnostic", "mdsched", "memory test", "ram test" }, EventPresets.MemoryDiag),
        (new[] { "thermal", "throttl", "overheat", "temperature", "too hot", "firmware limit" }, EventPresets.Thermal),
        (new[] { "startup", "boot", "restart", "who shut", "shutdowns and startups" }, EventPresets.Boot),
        (new[] { "error", "critical", "everything", "all events", "problems" }, EventPresets.Errors),
    };

    public static EventQuerySpec Parse(string input, DateTime? now = null)
    {
        var t = (input ?? "").ToLowerInvariant().Trim();
        var clock = now ?? DateTime.Now;
        var spec = new EventQuerySpec();
        var matched = new List<EventPreset>();

        // "crash" alone means system crashes (blue screens + unexpected shutdowns), unless it says app/game/gpu.
        bool genericCrash = Regex.IsMatch(t, @"\bcrash(es|ed)?\b") && !Regex.IsMatch(t, @"\b(app|application|program|game|gpu|graphics|driver)");
        foreach (var (words, preset) in Keywords)
        {
            if (words.Any(w => t.Contains(w)) && !matched.Contains(preset))
            {
                if (preset == EventPresets.Errors && matched.Count > 0) continue;
                if (preset == EventPresets.Boot && matched.Contains(EventPresets.UnexpectedShutdowns)) continue;
                matched.Add(preset);
            }
        }
        if (genericCrash)
        {
            if (!matched.Contains(EventPresets.BlueScreens)) matched.Insert(0, EventPresets.BlueScreens);
            if (!matched.Contains(EventPresets.UnexpectedShutdowns)) matched.Insert(1, EventPresets.UnexpectedShutdowns);
            matched.Remove(EventPresets.Errors);
        }

        // Explicit event IDs: "event 41", "id 1001"
        var ids = IdRegex().Matches(t).Select(m => int.Parse(m.Groups[1].Value)).ToArray();
        if (ids.Length > 0)
        {
            spec.Selectors.Add(new EventSelector(EventPresets.Sys, null, ids));
            spec.Selectors.Add(new EventSelector(EventPresets.App, null, ids));
        }
        foreach (var p in matched)
        {
            spec.Selectors.AddRange(p.Selectors);
            if (p.Levels != null && matched.Count == 1) spec.Levels.AddRange(p.Levels);
        }
        if (matched.Count == 0 && ids.Length == 0)
        {
            // Unknown words: search errors/warnings for the text.
            spec.Levels.AddRange(new[] { 1, 2, 3 });
            var cleaned = StripTime(t).Trim();
            foreach (var filler in new[] { "show me", "show", "find", "all", "the", "any", "events", "event", "logs", "log", "with", "about", "for", "from", "in", "please", "list" })
                cleaned = Regex.Replace(cleaned, $@"\b{filler}\b", " ");
            cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
            if (cleaned.Length > 0) spec.Text = cleaned;
        }

        // Quoted text always becomes a text filter.
        var quoted = Regex.Match(input ?? "", "\"([^\"]+)\"");
        if (quoted.Success) spec.Text = quoted.Groups[1].Value;

        (spec.Since, spec.Until, var when) = ParseTime(t, clock);
        var what = matched.Count > 0 ? string.Join(", ", matched.Select(m => m.Title.ToLowerInvariant())) : ids.Length > 0 ? $"event ID {string.Join("/", ids)}" : spec.Text != null ? $"errors & warnings mentioning \"{spec.Text}\"" : "errors & warnings";
        spec.Description = $"Showing {what}{when}";
        return spec;
    }

    private static string StripTime(string t) => TimeRegex().Replace(t, " ");

    public static (DateTime? Since, DateTime? Until, string Description) ParseTime(string t, DateTime now)
    {
        var m = Regex.Match(t, @"(?:last|past)\s+(\d+)\s*(minute|min|hour|hr|day|week|month|year)s?");
        if (m.Success)
        {
            int n = int.Parse(m.Groups[1].Value);
            var since = m.Groups[2].Value switch
            {
                "minute" or "min" => now.AddMinutes(-n),
                "hour" or "hr" => now.AddHours(-n),
                "day" => now.AddDays(-n),
                "week" => now.AddDays(-7 * n),
                "month" => now.AddMonths(-n),
                _ => now.AddYears(-n),
            };
            return (since, null, $" in the last {n} {m.Groups[2].Value}{(n == 1 ? "" : "s")}");
        }
        if (t.Contains("today")) return (now.Date, null, " today");
        if (t.Contains("yesterday")) return (now.Date.AddDays(-1), now.Date, " yesterday");
        if (Regex.IsMatch(t, @"(last|past|this)\s+week")) return (now.AddDays(-7), null, " in the last 7 days");
        if (Regex.IsMatch(t, @"(last|past|this)\s+month")) return (now.AddDays(-30), null, " in the last 30 days");
        if (Regex.IsMatch(t, @"(last|past|this)\s+year")) return (now.AddDays(-365), null, " in the last year");
        if (Regex.IsMatch(t, @"(last|past)\s+hour")) return (now.AddHours(-1), null, " in the last hour");
        if (Regex.IsMatch(t, @"\b(ever|all time|everything)\b") || Regex.IsMatch(t, @"\ball\b")) return (null, null, " (all time)");
        return (now.AddDays(-30), null, " in the last 30 days");
    }

    [GeneratedRegex(@"\b(?:event|id|event id)\s*#?\s*(\d{1,5})\b")]
    private static partial Regex IdRegex();

    [GeneratedRegex(@"(last|past|this)\s+(\d+\s*)?(minute|min|hour|hr|day|week|month|year)s?|today|yesterday|all time")]
    private static partial Regex TimeRegex();
}

/// <summary>Source of Windows events (implemented with EventLogReader on Windows).</summary>
public interface IEventSource
{
    List<EventEntry> Query(EventQuerySpec spec, int max = 500);
}
