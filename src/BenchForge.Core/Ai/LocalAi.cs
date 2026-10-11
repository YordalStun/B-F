using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BenchForge.Core.Persistence;

namespace BenchForge.Core.Ai;

public enum AiProviderKind
{
    /// <summary>No AI — built-in analysis only.</summary>
    None,
    /// <summary>Claude via the Anthropic API (needs an API key + internet).</summary>
    Claude,
    /// <summary>A model downloaded by BenchForge and run on this PC with llama.cpp (offline after download).</summary>
    LocalModel,
    /// <summary>An already-running OpenAI-compatible server (Ollama, LM Studio, llama.cpp …).</summary>
    CustomServer,
}

public sealed record LocalModelInfo(string Id, string Name, string Description, string Url, long SizeBytes, int MinRamGB, int ContextTokens)
{
    public string FileName => Url[(Url.LastIndexOf('/') + 1)..];
    public string Display => $"{Name} — {SizeBytes / 1e9:0.0} GB";
}

/// <summary>Models offered in the dropdown. All are GGUF Q4_K_M builds from Hugging Face.</summary>
public static class LocalModelCatalog
{
    public static readonly LocalModelInfo[] Models =
    {
        new("qwen2.5-3b", "Qwen 2.5 3B (fast)", "Small and quick. Works on almost any PC with 8 GB RAM.",
            "https://huggingface.co/bartowski/Qwen2.5-3B-Instruct-GGUF/resolve/main/Qwen2.5-3B-Instruct-Q4_K_M.gguf", 1_929_903_264, 8, 16384),
        new("llama3.2-3b", "Llama 3.2 3B (fast)", "Meta's small model. Good short answers, 8 GB RAM.",
            "https://huggingface.co/bartowski/Llama-3.2-3B-Instruct-GGUF/resolve/main/Llama-3.2-3B-Instruct-Q4_K_M.gguf", 2_019_377_696, 8, 16384),
        new("qwen3-4b", "Qwen 3 4B Instruct 2507 (recommended)", "Best quality for its size. 8 GB RAM.",
            "https://huggingface.co/unsloth/Qwen3-4B-Instruct-2507-GGUF/resolve/main/Qwen3-4B-Instruct-2507-Q4_K_M.gguf", 2_497_281_120, 8, 16384),
        new("gemma3-4b", "Gemma 3 4B", "Google's small model. 8 GB RAM.",
            "https://huggingface.co/unsloth/gemma-3-4b-it-GGUF/resolve/main/gemma-3-4b-it-Q4_K_M.gguf", 2_489_894_016, 8, 16384),
        new("phi3.5-mini", "Phi 3.5 Mini", "Microsoft's small reasoning model. 8 GB RAM.",
            "https://huggingface.co/bartowski/Phi-3.5-mini-instruct-GGUF/resolve/main/Phi-3.5-mini-instruct-Q4_K_M.gguf", 2_393_232_672, 8, 16384),
        new("qwen2.5-7b", "Qwen 2.5 7B (better)", "Noticeably smarter. 16 GB RAM or a GPU with 6 GB+.",
            "https://huggingface.co/bartowski/Qwen2.5-7B-Instruct-GGUF/resolve/main/Qwen2.5-7B-Instruct-Q4_K_M.gguf", 4_683_074_240, 16, 16384),
        new("llama3.1-8b", "Llama 3.1 8B (better)", "Strong general model. 16 GB RAM or a GPU with 6 GB+.",
            "https://huggingface.co/bartowski/Meta-Llama-3.1-8B-Instruct-GGUF/resolve/main/Meta-Llama-3.1-8B-Instruct-Q4_K_M.gguf", 4_920_739_232, 16, 16384),
        new("qwen3-8b", "Qwen 3 8B (better)", "Strong reasoning. 16 GB RAM or a GPU with 8 GB+.",
            "https://huggingface.co/unsloth/Qwen3-8B-GGUF/resolve/main/Qwen3-8B-Q4_K_M.gguf", 5_027_784_512, 16, 16384),
        new("qwen2.5-14b", "Qwen 2.5 14B (best, slow)", "Best local answers. 32 GB RAM or a GPU with 12 GB+.",
            "https://huggingface.co/bartowski/Qwen2.5-14B-Instruct-GGUF/resolve/main/Qwen2.5-14B-Instruct-Q4_K_M.gguf", 8_988_110_976, 32, 16384),
    };

    public static LocalModelInfo? Get(string? id) => Models.FirstOrDefault(m => m.Id == id);
}

public sealed record DownloadProgress(string What, long Done, long Total)
{
    public double Fraction => Total > 0 ? (double)Done / Total : 0;
    public override string ToString() => Total > 0 ? $"{What}: {Done / 1e6:0} / {Total / 1e6:0} MB ({Fraction:P0})" : $"{What}: {Done / 1e6:0} MB";
}

/// <summary>
/// Downloads and runs a local model with llama.cpp's llama-server (OpenAI-compatible HTTP API on localhost).
/// Everything is stored in BenchForgeData\AI so it travels with the USB stick.
/// </summary>
public sealed class LocalAiManager : IDisposable
{
    public const int Port = 18089;
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private Process? _server;
    private string? _loadedModel;

    static LocalAiManager()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("BenchForge/1.0");
    }

    public static string Folder => Directory.CreateDirectory(Path.Combine(DataPaths.Root, "AI")).FullName;
    public static string ModelsFolder => Directory.CreateDirectory(Path.Combine(Folder, "models")).FullName;
    public static string EngineFolder => Path.Combine(Folder, "llama.cpp");
    public static string ServerExe => Path.Combine(EngineFolder, OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server");

    public static bool IsModelDownloaded(LocalModelInfo m) => File.Exists(Path.Combine(ModelsFolder, m.FileName));
    public static bool IsEngineInstalled => File.Exists(ServerExe) || FindServerExe() != null;

    private static string? FindServerExe()
    {
        if (!Directory.Exists(EngineFolder)) return null;
        return Directory.GetFiles(EngineFolder, OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server", SearchOption.AllDirectories).FirstOrDefault();
    }

    /// <summary>Downloads the engine (if needed) and the model. Safe to call again — finished files are skipped, partial ones resume.</summary>
    public static async Task EnsureDownloadedAsync(LocalModelInfo model, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        if (!IsEngineInstalled) await DownloadEngineAsync(progress, ct);
        var path = Path.Combine(ModelsFolder, model.FileName);
        if (!File.Exists(path)) await DownloadFileAsync(model.Url, path, model.Name, model.SizeBytes, progress, ct);
    }

    public static async Task DownloadEngineAsync(IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Automatic engine download is for Windows. Install llama.cpp and put llama-server in " + EngineFolder);
        progress?.Report(new DownloadProgress("Finding latest llama.cpp", 0, 0));
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/ggml-org/llama.cpp/releases/latest");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var release = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))!;
        var assets = release["assets"]!.AsArray().Select(a => (Name: (string)a!["name"]!, Url: (string)a["browser_download_url"]!, Size: (long)a["size"]!)).ToList();
        // Vulkan build runs on NVIDIA, AMD and Intel GPUs and falls back to the CPU; then plain CPU builds.
        string[] preference = { "bin-win-vulkan-x64.zip", "bin-win-cpu-x64.zip", "bin-win-avx2-x64.zip", "bin-win-noavx-x64.zip" };
        var asset = preference.Select(p => assets.FirstOrDefault(a => a.Name.EndsWith(p, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(a => a.Name != null);
        if (asset.Name == null) throw new InvalidOperationException("Could not find a Windows build of llama.cpp in the latest release.");
        var zip = Path.Combine(Folder, asset.Name);
        await DownloadFileAsync(asset.Url, zip, "AI engine (llama.cpp)", asset.Size, progress, ct);
        progress?.Report(new DownloadProgress("Unpacking AI engine", 0, 0));
        if (Directory.Exists(EngineFolder)) Directory.Delete(EngineFolder, true);
        ZipFile.ExtractToDirectory(zip, EngineFolder);
        File.Delete(zip);
        if (FindServerExe() == null) throw new InvalidOperationException("llama-server.exe was not found in the downloaded package.");
    }

    private static async Task DownloadFileAsync(string url, string path, string what, long expectedSize, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var part = path + ".part";
        long existing = File.Exists(part) ? new FileInfo(part).Length : 0;
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (existing > 0 && resp.StatusCode != HttpStatusCode.PartialContent) existing = 0; // server ignored the range: restart
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength is long len ? len + existing : expectedSize;
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = new FileStream(part, existing > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            var buf = new byte[1 << 20];
            long done = existing;
            var sw = Stopwatch.StartNew();
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (sw.ElapsedMilliseconds > 250) { progress?.Report(new DownloadProgress(what, done, total)); sw.Restart(); }
            }
            progress?.Report(new DownloadProgress(what, done, total));
        }
        File.Move(part, path, true);
    }

    public static void DeleteModel(LocalModelInfo m)
    {
        var p = Path.Combine(ModelsFolder, m.FileName);
        if (File.Exists(p)) File.Delete(p);
    }

    /// <summary>Starts llama-server with the chosen model (GPU offload when possible, CPU otherwise).</summary>
    public async Task<string> StartAsync(LocalModelInfo model, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var baseUrl = $"http://127.0.0.1:{Port}";
        if (_server is { HasExited: false } && _loadedModel == model.Id) return baseUrl;
        Stop();
        await EnsureDownloadedAsync(model, progress, ct);
        var exe = FindServerExe() ?? ServerExe;
        var modelPath = Path.Combine(ModelsFolder, model.FileName);
        foreach (var gpuLayers in new[] { 99, 0 })
        {
            progress?.Report(new DownloadProgress(gpuLayers > 0 ? "Loading model on GPU" : "Loading model on CPU", 0, 0));
            var psi = new ProcessStartInfo(exe, $"-m \"{modelPath}\" --port {Port} --host 127.0.0.1 -c {model.ContextTokens} -ngl {gpuLayers} --no-webui")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
            };
            var p = Process.Start(psi)!;
            p.OutputDataReceived += (_, _) => { };
            p.ErrorDataReceived += (_, _) => { };
            p.BeginOutputReadLine(); p.BeginErrorReadLine();
            var until = DateTime.Now.AddMinutes(3);
            while (DateTime.Now < until && !p.HasExited && !ct.IsCancellationRequested)
            {
                try
                {
                    using var h = await Http.GetAsync(baseUrl + "/health", ct);
                    if (h.IsSuccessStatusCode) { _server = p; _loadedModel = model.Id; return baseUrl; }
                }
                catch (HttpRequestException) { }
                await Task.Delay(500, ct);
            }
            try { if (!p.HasExited) p.Kill(true); } catch { }
            ct.ThrowIfCancellationRequested();
        }
        throw new InvalidOperationException("The local AI engine could not start. Try a smaller model or check free RAM.");
    }

    public void Stop()
    {
        try { if (_server is { HasExited: false }) _server.Kill(true); } catch { }
        _server = null;
        _loadedModel = null;
    }

    public void Dispose() => Stop();
}

/// <summary>Talks to any OpenAI-compatible chat endpoint (llama-server, Ollama, LM Studio).</summary>
public static class OpenAiCompatibleChat
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };

    public static async Task<string> AskAsync(string baseUrl, string? model, string system, string user, CancellationToken ct)
    {
        var url = baseUrl.TrimEnd('/');
        if (!url.EndsWith("/v1")) url += "/v1";
        var body = new
        {
            model = string.IsNullOrWhiteSpace(model) ? "local" : model,
            messages = new object[] { new { role = "system", content = system }, new { role = "user", content = user } },
            temperature = 0.3,
            max_tokens = 2048,
            stream = false,
        };
        using var resp = await Http.PostAsJsonAsync(url + "/chat/completions", body, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) return $"The local AI returned an error ({(int)resp.StatusCode}): {text[..Math.Min(400, text.Length)]}";
        var node = JsonNode.Parse(text);
        var answer = (string?)node?["choices"]?[0]?["message"]?["content"] ?? "";
        // Some reasoning models wrap thoughts in <think> tags.
        int end = answer.IndexOf("</think>", StringComparison.Ordinal);
        if (end >= 0) answer = answer[(end + 8)..];
        return answer.Trim().Length > 0 ? answer.Trim() : "The local AI returned an empty answer.";
    }
}
