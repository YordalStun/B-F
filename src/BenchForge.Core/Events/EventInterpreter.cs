using System.Globalization;
using System.Text.RegularExpressions;
using BenchForge.Core.Crash;
using BenchForge.Core.Model;

namespace BenchForge.Core.Events;

/// <summary>Turns raw Windows events into crash-relevant facts, with a plain-English meaning.</summary>
public static partial class EventInterpreter
{
    public static CrashSignal? Interpret(EventEntry e)
    {
        var p = e.Provider;
        var msg = e.Message ?? "";
        CrashSignal S(SignalKind k, string title, string detail = "") => new()
        {
            Kind = k, Time = e.Time, Title = title, Detail = detail, EventId = e.Id, Provider = e.Provider,
        };

        if (Is(p, "Microsoft-Windows-Kernel-Power") && e.Id == 41)
        {
            uint code = ParseUInt(Get(e, "BugcheckCode", 0));
            bool powerButton = ParseULong(Get(e, "PowerButtonTimestamp", 5)) != 0;
            if (code != 0)
            {
                var s = S(SignalKind.BugCheck, $"Crash: {BugCheckKnowledgeBase.Describe(code)}", "Windows rebooted after a blue screen.");
                s.BugCheckCode = code;
                s.Component = BugCheckKnowledgeBase.Get(code)?.LikelyComponent;
                return s;
            }
            return S(SignalKind.UnexpectedShutdown, powerButton ? "Hard reset (power button held)" : "Unexpected power loss / hard reset",
                powerButton ? "The power button was held to switch the PC off — usually because it froze."
                            : "The PC lost power or was reset without a blue screen: freeze → reset, PSU cut-out, or a thermal/over-current shutdown.");
        }
        if ((Is(p, "Microsoft-Windows-WER-SystemErrorReporting") || Is(p, "BugCheck")) && e.Id == 1001
            || Is(p, "Windows Error Reporting") && e.Id == 1001 && msg.Contains("BlueScreen", StringComparison.OrdinalIgnoreCase))
        {
            var m = BugCheckRegex().Match(msg);
            uint code = 0; var pars = new ulong[4];
            if (m.Success)
            {
                code = (uint)Convert.ToUInt64(m.Groups[1].Value, 16);
                var ps = m.Groups[2].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < Math.Min(4, ps.Length); i++) pars[i] = ParseULong(ps[i]);
            }
            else if (e.Properties.Count > 0)
            {
                code = ParseUInt(e.Properties[0]);
            }
            if (code == 0 && !Is(p, "Windows Error Reporting")) return S(SignalKind.BugCheck, "Blue screen (code unknown)", msg);
            if (code == 0) return null;
            var info = BugCheckKnowledgeBase.Get(code);
            var extra = BugCheckKnowledgeBase.DescribeParameters(code, pars);
            var sig = S(SignalKind.BugCheck, $"Blue screen: {BugCheckKnowledgeBase.Describe(code)}", (info?.Meaning ?? "") + (extra != null ? " " + extra + "." : ""));
            sig.BugCheckCode = code;
            sig.Component = info?.LikelyComponent;
            var dump = Regex.Match(msg, @"saved in:\s*(.+?\.dmp)", RegexOptions.IgnoreCase);
            if (dump.Success) sig.Detail += $" Dump: {dump.Groups[1].Value.Trim()}";
            return sig;
        }
        if (Is(p, "EventLog") && e.Id == 6008)
            return S(SignalKind.UnexpectedShutdown, "Previous shutdown was unexpected", FirstLine(msg));

        if (Is(p, "Microsoft-Windows-WHEA-Logger"))
        {
            bool fatal = e.Level is 1 or 2 || e.Id is 1 or 18 or 20 or 46;
            string? comp = Field(msg, "Component") ?? Field(msg, "Reported by component");
            string? source = Field(msg, "Error Source");
            string? type = Field(msg, "Error Type");
            string? apic = Field(msg, "Processor APIC ID");
            string? device = Field(msg, "Device Name") ?? Field(msg, "Primary Device Name");
            var detail = string.Join(" · ", new[] { comp, source, type, apic != null ? $"APIC ID {apic}" : null, device }.Where(x => !string.IsNullOrWhiteSpace(x)));
            var s = S(fatal ? SignalKind.WheaFatal : SignalKind.WheaCorrected, fatal ? "Fatal hardware error (WHEA)" : "Corrected hardware error (WHEA)", detail);
            s.HardwareSource = (comp ?? "") + (apic != null ? $" (APIC ID {apic})" : "");
            s.Component = MapWheaComponent(comp, source, type);
            return s;
        }
        if (Is(p, "Display") && e.Id == 4101 || Is(p, "nvlddmkm") || Is(p, "amdkmdag") || Is(p, "amdkmdap") || Is(p, "igfx") || Is(p, "igfxn"))
        {
            var m = Regex.Match(msg, @"Display driver (\S+) stopped responding", RegexOptions.IgnoreCase);
            var s = S(SignalKind.GpuDriverReset, "GPU driver reset (TDR)", FirstLine(msg));
            s.Module = m.Success ? m.Groups[1].Value : p == "Display" ? null : p;
            s.Component = Component.Gpu;
            return s;
        }
        if (Is(p, "Application Error") && e.Id == 1000)
        {
            string app = e.Properties.ElementAtOrDefault(0) ?? Field(msg, "Faulting application name")?.Split(',')[0] ?? "?";
            string module = e.Properties.ElementAtOrDefault(3) ?? Field(msg, "Faulting module name")?.Split(',')[0] ?? "?";
            string code = e.Properties.ElementAtOrDefault(6) ?? Field(msg, "Exception code") ?? "";
            var s = S(SignalKind.AppCrash, $"{app} crashed", $"Faulting module {module}{(code != "" ? $", exception {code}" : "")}");
            s.Module = module;
            if (module.StartsWith("nvwgf", StringComparison.OrdinalIgnoreCase) || module.StartsWith("nvoglv", StringComparison.OrdinalIgnoreCase) || module.StartsWith("atio", StringComparison.OrdinalIgnoreCase) || module.StartsWith("amdxx", StringComparison.OrdinalIgnoreCase) || module.StartsWith("igxel", StringComparison.OrdinalIgnoreCase))
                s.Component = Component.Gpu;
            return s;
        }
        if (Is(p, "Application Hang") && e.Id == 1002)
            return S(SignalKind.AppCrash, $"{e.Properties.ElementAtOrDefault(0) ?? "Program"} stopped responding", FirstLine(msg));

        if (Is(p, "disk") || Is(p, "Ntfs") || Is(p, "Microsoft-Windows-Ntfs") || Is(p, "stornvme") || Is(p, "storahci") || Is(p, "iaStorA") || Is(p, "volmgr"))
        {
            if (e.Level is 4 or 0) return null;
            var s = S(SignalKind.DiskError, e.Id switch
            {
                7 => "Bad block on drive",
                11 => "Drive controller error",
                51 => "Paging error on drive",
                153 => "Drive I/O retried",
                129 => "Storage controller reset",
                55 or 98 => "File system corruption",
                161 => "Crash dump could not be written",
                _ => "Drive / file system error",
            }, FirstLine(msg));
            s.Component = Component.Storage;
            s.Module = p;
            return s;
        }
        if (Is(p, "Service Control Manager") && e.Id is 7031 or 7034 or 7000 or 7001 or 7026 or 7023)
            return S(SignalKind.ServiceFailure, "Service / driver failure", FirstLine(msg));
        if (Is(p, "Microsoft-Windows-Kernel-Processor-Power") && e.Id == 37)
        {
            var s = S(SignalKind.ThermalThrottle, "CPU speed limited by firmware", FirstLine(msg));
            s.Component = Component.Cpu;
            return s;
        }
        if (Is(p, "Microsoft-Windows-MemoryDiagnostics-Results"))
        {
            bool bad = e.Id is 1102 or 1202 || msg.Contains("detected hardware errors", StringComparison.OrdinalIgnoreCase);
            var s = S(SignalKind.MemoryDiagnostic, bad ? "Windows Memory Diagnostic FOUND RAM errors" : "Windows Memory Diagnostic: no errors", FirstLine(msg));
            s.Component = Component.Memory;
            return s;
        }
        return null;
    }

    /// <summary>A short "what this means" for the event viewer detail pane.</summary>
    public static string Explain(EventEntry e)
    {
        var s = Interpret(e);
        if (s == null)
            return e.Level is 1 or 2 ? "An error was logged by " + e.Provider + ". On its own it is not necessarily related to crashes." : "Informational event.";
        var text = s.Title + (string.IsNullOrEmpty(s.Detail) ? "" : " — " + s.Detail);
        if (s.BugCheckCode is uint code && BugCheckKnowledgeBase.Get(code) is BugCheckInfo kb)
        {
            text += "\n\nLikely causes:\n• " + string.Join("\n• ", kb.Causes);
            if (kb.Fixes.Length > 0) text += "\n\nWhat to try:\n• " + string.Join("\n• ", kb.Fixes);
        }
        else
        {
            text += s.Kind switch
            {
                SignalKind.WheaFatal => "\n\nA fatal WHEA error is the hardware itself reporting a failure. Most common causes: overclock/undervolt, XMP/EXPO memory settings, overheating, or failing CPU/RAM/PCIe device.",
                SignalKind.WheaCorrected => "\n\nThe hardware detected and fixed an error. A few are harmless; many (especially from the same core or PCIe device) mean something is marginal — e.g. PCIe link problems, unstable overclock or RAM.",
                SignalKind.UnexpectedShutdown => "\n\nNo blue screen was recorded. That points to: the PC freezing and being reset, the power supply shutting off (overload/fault), a thermal shutdown, or power cuts.",
                SignalKind.GpuDriverReset => "\n\nWindows reset the graphics driver because the GPU stopped responding. Usually an unstable GPU overclock, overheating, a driver bug, or power delivery to the GPU.",
                SignalKind.DiskError => "\n\nDrive errors can mean a failing drive, a loose/bad cable, or a controller/firmware problem. Back up important data and check SMART health.",
                SignalKind.ThermalThrottle => "\n\nThe CPU was slowed down by the motherboard firmware, usually because of heat or power limits.",
                _ => "",
            };
        }
        return text;
    }

    public static Component? MapWheaComponent(string? comp, string? source, string? type)
    {
        var all = $"{comp} {source} {type}".ToLowerInvariant();
        if (all.Contains("memory") && !all.Contains("cache")) return Component.Memory;
        if (all.Contains("processor") || all.Contains("cache") || all.Contains("machine check") || all.Contains("bus/interconnect")) return Component.Cpu;
        if (all.Contains("pci express") || all.Contains("pcie")) return null; // could be GPU or NVMe
        return null;
    }

    private static bool Is(string provider, string name) => string.Equals(provider, name, StringComparison.OrdinalIgnoreCase);

    private static string Get(EventEntry e, string name, int index) =>
        e.Data.TryGetValue(name, out var v) ? v : e.Properties.ElementAtOrDefault(index) ?? "";

    private static string? Field(string msg, string name)
    {
        var m = Regex.Match(msg, "^\\s*" + Regex.Escape(name) + "\\s*:\\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private static string FirstLine(string msg) => msg.Split('\n', 2)[0].Trim();

    public static uint ParseUInt(string s) => (uint)(ParseULong(s) & 0xFFFFFFFF);

    public static ulong ParseULong(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ulong.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h) ? h : 0;
        return ulong.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) ? d
             : long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var neg) ? unchecked((ulong)neg) : 0;
    }

    [GeneratedRegex(@"bugcheck was:\s*0x([0-9a-fA-F]+)\s*\(([^)]*)\)", RegexOptions.IgnoreCase)]
    private static partial Regex BugCheckRegex();
}
