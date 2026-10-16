using System.Reflection;

namespace BenchForge.Gpu;

/// <summary>Shaders embedded in the assembly: HLSL text for Direct3D, precompiled SPIR-V for Vulkan.</summary>
internal static class ShaderSource
{
    public static string Hlsl()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("scenes.hlsl") ?? throw new InvalidOperationException("scenes.hlsl missing");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    public static byte[] Spirv(string entry)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("spv." + entry) ?? throw new InvalidOperationException($"SPIR-V for {entry} missing");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
