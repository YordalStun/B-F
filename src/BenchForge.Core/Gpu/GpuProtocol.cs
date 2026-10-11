using System.Text.Json;
using System.Text.Json.Serialization;

namespace BenchForge.Core.Gpu;

/// <summary>One JSON line written by the 3D test process to stdout.</summary>
public sealed class GpuMessage
{
    [JsonPropertyName("t")] public string Type { get; set; } = "";
    [JsonPropertyName("scene")] public string? Scene { get; set; }
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("frac")] public double Fraction { get; set; }
    [JsonPropertyName("fps")] public double Fps { get; set; }
    [JsonPropertyName("frames")] public long Frames { get; set; }
    [JsonPropertyName("seconds")] public double Seconds { get; set; }
    [JsonPropertyName("avgFps")] public double AvgFps { get; set; }
    [JsonPropertyName("low1Fps")] public double Low1Fps { get; set; }
    [JsonPropertyName("p99Ms")] public double P99Ms { get; set; }
    [JsonPropertyName("errors")] public long Errors { get; set; }
    [JsonPropertyName("checked")] public long Checked { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("renderer")] public string? Renderer { get; set; }
    [JsonPropertyName("vendor")] public string? Vendor { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }

    private static readonly JsonSerializerOptions Opts = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault };

    public string ToLine() => JsonSerializer.Serialize(this, Opts);

    public static GpuMessage? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line[0] != '{') return null;
        try { return JsonSerializer.Deserialize<GpuMessage>(line, Opts); } catch { return null; }
    }
}

public static class GpuScenes
{
    public const string Geometry = "Geometry";
    public const string Shading = "Shading";
    public const string Compute = "Compute";
    public const string Fill = "Fill-rate";
    public const string Vram = "VRAM check";

    public static readonly string[] Benchmark = { Geometry, Shading, Compute, Fill };

    public static string Describe(string scene) => scene switch
    {
        Geometry => "Tens of thousands of lit, instanced 3D objects with a moving camera (vertex & raster throughput).",
        Shading => "Ray-marched 3D landscape with soft shadows, ambient occlusion and fog (pixel shader maths).",
        Compute => "One million particle physics simulation in a compute shader, rendered as points (compute + memory).",
        Fill => "Many layers of blended, textured full-screen passes at high resolution (fill-rate & VRAM bandwidth).",
        Vram => "Writes known patterns into video memory with a compute shader and reads them back (detects VRAM errors / artifacts).",
        _ => scene,
    };
}
