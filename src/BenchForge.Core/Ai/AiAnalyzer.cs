using System.Text;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;
using BenchForge.Core.Analysis;
using BenchForge.Core.Crash;
using BenchForge.Core.Model;

namespace BenchForge.Core.Ai;

/// <summary>
/// Optional AI analysis using Claude with the user's own API key. Sends a compact text summary of the selected
/// runs/crashes (scores, sensor peaks, errors, Windows events, the offline findings) — never dump files.
/// </summary>
public sealed class AiAnalyzer
{
    public const string DefaultModel = "claude-opus-5-5";
    private readonly string _apiKey;
    private readonly string _model;

    public AiAnalyzer(string apiKey, string? model = null)
    {
        _apiKey = apiKey;
        _model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model;
    }

    public const string SystemPrompt =
        "You are a senior PC hardware technician inside the BenchForge diagnostics app. " +
        "You receive benchmark/stress-test results, sensor readings, crash data and Windows event log entries from one or more test runs. " +
        "Find the most likely root cause, using evidence from the data (quote the specific numbers, stop codes, event IDs and times you rely on). " +
        "Look for patterns shared between runs. Be honest about uncertainty and say what extra test would confirm it. " +
        "Write for a capable home user: short sections with headings — 'Most likely cause', 'Evidence', 'What to try (in order)', 'Other possibilities'. " +
        "Give concrete BIOS/driver/hardware steps. Plain text with simple markdown headings and bullet points.";

    public static string BuildPrompt(string question, string context) =>
        $"{context}\n\n=== QUESTION ===\n{(string.IsNullOrWhiteSpace(question) ? "What is wrong with this PC and how do I fix it? If several runs are included, compare them and find the pattern." : question)}";

    public async Task<string> AskAsync(string question, IReadOnlyList<RunResult> runs, IReadOnlyList<SystemCrash>? crashes = null, IReadOnlyList<EventEntry>? events = null, CancellationToken ct = default)
    {
        var prompt = BuildPrompt(question, BuildContext(runs, crashes, events));

        var client = new AnthropicClient { ApiKey = _apiKey };
        try
        {
            var response = await client.Beta.Messages.Create(new MessageCreateParams
            {
                Model = _model,
                MaxTokens = 16000,
                System = SystemPrompt,
                Thinking = new BetaThinkingConfigAdaptive(),
                OutputConfig = new BetaOutputConfig { Effort = Effort.High },
                Betas = [AnthropicBeta.ServerSideFallback2026_06_01],
                Fallbacks = new List<BetaFallbackParam> { new() { Model = "claude-opus-4-8" } },
                Messages = [new() { Role = Role.User, Content = prompt }],
            }, ct);

            if (response.StopReason?.Raw() == "refusal")
                return "The AI declined to answer this request. The built-in analysis above still applies.";
            var sb = new StringBuilder();
            foreach (var block in response.Content)
                if (block.TryPickText(out var text)) sb.Append(text.Text);
            var answer = sb.ToString().Trim();
            if (response.StopReason?.Raw() == "max_tokens") answer += "\n\n(Answer was cut off because it reached the length limit.)";
            return answer.Length > 0 ? answer : "The AI returned an empty answer.";
        }
        catch (AnthropicUnauthorizedException)
        {
            return "The API key was rejected. Check it in Settings (create one at console.anthropic.com).";
        }
        catch (AnthropicRateLimitException)
        {
            return "The AI service is rate-limiting requests right now. Wait a minute and try again.";
        }
        catch (AnthropicBadRequestException ex)
        {
            return "The AI request was rejected: " + ex.Message;
        }
        catch (Anthropic5xxException)
        {
            return "The AI service had a temporary problem. Try again in a moment.";
        }
        catch (AnthropicIOException)
        {
            return "Could not reach the AI service. Check the internet connection — the built-in analysis works offline.";
        }
        catch (AnthropicApiException ex)
        {
            return "AI error: " + ex.Message;
        }
    }

    /// <summary>The compact, human-readable data summary sent to the AI (also handy to copy/paste anywhere).</summary>
    public static string BuildContext(IReadOnlyList<RunResult> runs, IReadOnlyList<SystemCrash>? crashes = null, IReadOnlyList<EventEntry>? events = null, int maxChars = 150_000)
    {
        var sb = new StringBuilder();
        var hw = runs.Select(r => r.Hardware).FirstOrDefault(h => h != null);
        if (hw != null)
        {
            sb.AppendLine("=== HARDWARE ===");
            sb.AppendLine($"CPU: {hw.Cpu.Name} ({hw.Cpu.Cores}C/{hw.Cpu.Threads}T, max {hw.Cpu.MaxClockMHz} MHz)");
            foreach (var g in hw.Gpus) sb.AppendLine($"GPU: {g.Name}, {g.VramGB:0.#} GB, driver {g.DriverVersion} ({g.DriverDate:yyyy-MM-dd})");
            sb.AppendLine($"RAM: {hw.TotalMemoryGB:0.#} GB in {hw.MemoryModules.Count} modules: " + string.Join("; ", hw.MemoryModules.Select(m => $"{m.Slot} {m.CapacityGB:0}GB {m.Manufacturer} {m.PartNumber} rated {m.SpeedMTs} running {m.ConfiguredSpeedMTs} MT/s")));
            foreach (var d in hw.Disks) sb.AppendLine($"Drive: {d.Model} {d.SizeGB:0} GB {d.BusType} {d.MediaType}, health {d.Health}{(d.TemperatureC != null ? $", {d.TemperatureC:0} °C" : "")}{(d.WearPercent != null ? $", wear {d.WearPercent:0}%" : "")}");
            sb.AppendLine($"Motherboard: {hw.Motherboard}, BIOS {hw.BiosVersion} ({hw.BiosDate:yyyy-MM-dd}); OS: {hw.OsName} {hw.OsVersion}; power plan: {hw.PowerPlan}");
        }

        foreach (var (r, i) in runs.Select((r, i) => (r, i)))
        {
            sb.AppendLine();
            sb.AppendLine($"=== RUN {i + 1}: {r.DisplayName} — {r.StartTime:yyyy-MM-dd HH:mm} — {r.Status} — PC {r.ImportedFrom ?? r.MachineName} ===");
            sb.AppendLine($"Duration {TimeFormat.Short(r.Duration)}; overall score {r.OverallScore?.ToString("0") ?? "n/a"}; " + string.Join(", ", r.ComponentScores.Select(kv => $"{kv.Key.Display()} {kv.Value:0}")));
            if (r.AbortReason != null) sb.AppendLine("Stopped: " + r.AbortReason);
            foreach (var s in r.Steps)
            {
                sb.AppendLine($"- Step '{s.Name}' ({s.Kind}, {string.Join("+", s.Components.Select(c => c.Display()))}): {s.Status}");
                foreach (var m in s.Modules)
                {
                    var subs = string.Join("; ", m.SubScores.Take(16).Select(x => $"{x.Name} {x.Value:0.##} {x.Unit}"));
                    if (subs.Length > 0) sb.AppendLine($"    {m.Component.Display()}: {subs}");
                    if (m.ErrorCount > 0 || m.Failed) sb.AppendLine($"    {m.Component.Display()} errors: {m.ErrorCount}{(m.Failed ? " FAILED: " + m.FailureReason : "")}");
                    foreach (var kv in m.Info.Where(kv => kv.Key.Contains("Error") || kv.Key.Contains("Failing") || kv.Key.Contains("Renderer") || kv.Key.Contains("Throughput") || kv.Key.Contains("integrity")))
                        sb.AppendLine($"    {kv.Key}: {kv.Value}");
                }
            }
            if (r.KeyStats.Count > 0)
                sb.AppendLine("Sensors (min/avg/max): " + string.Join("; ", r.KeyStats.Select(kv => $"{kv.Value.Name} {kv.Value.Min:0.#}/{kv.Value.Avg:0.#}/{kv.Value.Max:0.#} {kv.Value.Unit}")));
            foreach (var e in r.Errors.Take(15)) sb.AppendLine($"Error {e.Time:HH:mm:ss} {e.Component.Display()} {e.Test}: {e.Message} {e.Detail}");
            if (r.TotalErrorCount > 15) sb.AppendLine($"... {r.TotalErrorCount} errors in total");
            if (r.Crash is CrashInfo c) AppendCrash(sb, c);
            foreach (var f in r.Findings.Where(f => f.Severity >= Severity.Warning)) sb.AppendLine($"Finding [{f.Severity}]: {f.Title} — {f.Detail}");
            foreach (var e in r.RelatedEvents.Take(25)) sb.AppendLine($"Event {e.Time:yyyy-MM-dd HH:mm:ss} {e.Log}/{e.Provider} ID {e.Id} {e.LevelName}: {Trim(e.Message, 300)}");
        }

        if (crashes is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("=== WINDOWS CRASHES ===");
            foreach (var c in crashes) { sb.AppendLine($"--- {c.Time:yyyy-MM-dd HH:mm} ---"); AppendCrash(sb, c.Info); }
        }
        if (events is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("=== EVENTS ===");
            foreach (var e in events.Take(60)) sb.AppendLine($"{e.Time:yyyy-MM-dd HH:mm:ss} {e.Log}/{e.Provider} ID {e.Id} {e.LevelName}: {Trim(e.Message, 300)}");
        }

        var cases = runs.Where(r => r.IsFailure).Select(FailureCase.FromRun).Concat(crashes?.Select(FailureCase.FromSystemCrash) ?? Enumerable.Empty<FailureCase>()).ToList();
        if (cases.Count > 0)
        {
            var rep = PatternAnalyzer.Analyze(cases, runs.Where(r => !r.IsFailure).ToList());
            sb.AppendLine();
            sb.AppendLine("=== BENCHFORGE OFFLINE PATTERN ANALYSIS ===");
            sb.AppendLine(rep.Verdict);
            foreach (var f in rep.Findings) sb.AppendLine($"- [{f.Severity}, {f.Confidence:P0}] {f.Title}: {f.Detail}");
        }
        var s2 = sb.ToString();
        return s2.Length > maxChars ? s2[..maxChars] + "\n...(truncated)" : s2;
    }

    private static void AppendCrash(StringBuilder sb, CrashInfo c)
    {
        sb.AppendLine($"CRASH: {c.Kind} — {c.Summary}; last alive {c.LastHeartbeat:yyyy-MM-dd HH:mm:ss}; rebooted {c.SystemRebooted}");
        if (c.StepName != null) sb.AppendLine($"During step '{c.StepName}' ({string.Join("+", c.ActiveComponents.Select(x => x.Display()))}), {c.RunElapsedSeconds / 60:0.0} min into run, {c.ErrorsBeforeCrash} errors before crash");
        if (c.PeakMetrics.Count > 0) sb.AppendLine("Peak sensors in final minute: " + string.Join("; ", c.PeakMetrics.Select(kv => $"{KeyMetrics.Label(kv.Key)} {kv.Value:0.#}{KeyMetrics.Unit(kv.Key)}")));
        foreach (var s in c.Signals.Take(20)) sb.AppendLine($"Signal {s.Time:HH:mm:ss} {s.Kind}: {s.Title} {s.Detail} {(s.Module != null ? "module " + s.Module : "")}");
        foreach (var d in c.Dumps) sb.AppendLine($"Dump {Path.GetFileName(d.Path)} {d.Created:yyyy-MM-dd HH:mm}: {(d.Valid ? BugCheckKnowledgeBase.Describe(d.BugCheckCode) + $" params {string.Join(", ", d.Parameters.Select(p => $"0x{p:X}"))}" : d.Error)} {d.DeepAnalysis}");
        foreach (var e in c.Explanation) sb.AppendLine("  " + e);
    }

    private static string Trim(string s, int n) { s = s.Replace("\r", " ").Replace("\n", " "); return s.Length > n ? s[..n] + "…" : s; }
}
