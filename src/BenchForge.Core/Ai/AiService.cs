using BenchForge.Core.Crash;
using BenchForge.Core.Model;

namespace BenchForge.Core.Ai;

public sealed class AiSettings
{
    public AiProviderKind Provider { get; set; } = AiProviderKind.None;
    /// <summary>Kept in memory only; saved encrypted by the app.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string? ClaudeApiKey { get; set; }
    public string ClaudeModel { get; set; } = AiAnalyzer.DefaultModel;
    public string LocalModelId { get; set; } = "qwen3-4b";
    public string CustomServerUrl { get; set; } = "http://localhost:11434";
    public string CustomServerModel { get; set; } = "llama3.1";
}

/// <summary>One entry point for "Ask AI" whichever provider is chosen.</summary>
public sealed class AiService : IDisposable
{
    private readonly LocalAiManager _local = new();

    public static string Describe(AiSettings s) => s.Provider switch
    {
        AiProviderKind.Claude => $"Claude ({s.ClaudeModel})",
        AiProviderKind.LocalModel => $"Local: {LocalModelCatalog.Get(s.LocalModelId)?.Name ?? s.LocalModelId}",
        AiProviderKind.CustomServer => $"Server {s.CustomServerUrl} ({s.CustomServerModel})",
        _ => "Off",
    };

    public async Task<string> AskAsync(AiSettings s, string question, IReadOnlyList<RunResult> runs, IReadOnlyList<SystemCrash>? crashes,
        IReadOnlyList<EventEntry>? events, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        switch (s.Provider)
        {
            case AiProviderKind.Claude:
                if (string.IsNullOrWhiteSpace(s.ClaudeApiKey)) return "No Claude API key set. Add one in Settings, or choose a local model instead.";
                return await new AiAnalyzer(s.ClaudeApiKey, s.ClaudeModel).AskAsync(question, runs, crashes, events, ct);

            case AiProviderKind.LocalModel:
            {
                var model = LocalModelCatalog.Get(s.LocalModelId) ?? LocalModelCatalog.Models[0];
                try
                {
                    var url = await _local.StartAsync(model, progress, ct);
                    progress?.Report(new DownloadProgress("Thinking (local model)", 0, 0));
                    // Small models have small context windows: send a shorter summary.
                    var prompt = AiAnalyzer.BuildPrompt(question, AiAnalyzer.BuildContext(runs, crashes, events, maxChars: 28_000));
                    return await OpenAiCompatibleChat.AskAsync(url, model.Id, AiAnalyzer.SystemPrompt, prompt, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { return "Local AI failed: " + ex.Message; }
            }

            case AiProviderKind.CustomServer:
                try
                {
                    progress?.Report(new DownloadProgress("Asking " + s.CustomServerUrl, 0, 0));
                    var prompt = AiAnalyzer.BuildPrompt(question, AiAnalyzer.BuildContext(runs, crashes, events, maxChars: 28_000));
                    return await OpenAiCompatibleChat.AskAsync(s.CustomServerUrl, s.CustomServerModel, AiAnalyzer.SystemPrompt, prompt, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { return $"Could not reach {s.CustomServerUrl}: {ex.Message}"; }

            default:
                return "AI is turned off. Choose Claude or a local model in Settings — the built-in analysis works without it.";
        }
    }

    public void Dispose() => _local.Dispose();
}
