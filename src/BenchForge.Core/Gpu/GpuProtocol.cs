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
    /// <summary>Graphics API the worker runs on (see <see cref="GpuApis"/>).</summary>
    [JsonPropertyName("api")] public string? Api { get; set; }
    /// <summary>PCIe transfer test: GB/s from PC to graphics card and back.</summary>
    [JsonPropertyName("upGBs")] public double UploadGBs { get; set; }
    [JsonPropertyName("downGBs")] public double DownloadGBs { get; set; }

    private static readonly JsonSerializerOptions Opts = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault };

    public string ToLine() => JsonSerializer.Serialize(this, Opts);

    public static GpuMessage? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line[0] != '{') return null;
        try { return JsonSerializer.Deserialize<GpuMessage>(line, Opts); } catch { return null; }
    }
}

/// <summary>Graphics APIs the 3D test can run on. The same scenes and work per frame run on each.</summary>
public static class GpuApis
{
    public const string OpenGL = "gl";
    public const string D3D11 = "dx11";
    public const string D3D12 = "dx12";
    public const string Vulkan = "vulkan";
    public static readonly string[] All = { D3D12, D3D11, Vulkan, OpenGL };

    public static string Display(string api) => api switch
    {
        OpenGL => "OpenGL 4.3",
        D3D11 => "DirectX 11",
        D3D12 => "DirectX 12",
        Vulkan => "Vulkan",
        _ => api,
    };
}

public static class GpuScenes
{
    public const string Geometry = "Geometry";
    public const string Shading = "Shading";
    public const string Compute = "Compute";
    public const string Fill = "Fill-rate";
    public const string Vram = "VRAM check";
    public const string Pcie = "PCIe transfer";

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
