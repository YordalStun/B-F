using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Windowing;

namespace BenchForge.Gpu;

/// <summary>
/// One graphics API (OpenGL, Direct3D 11/12, Vulkan). Every backend draws the same scenes with the same amount of work per
/// frame into a fixed-size off-screen image, then scales that image into the window. Timing, scheduling and reporting are
/// shared in <see cref="GpuProgram"/>.
/// </summary>
internal interface IGpuBackend : IDisposable
{
    string Api { get; }
    string Renderer { get; }
    string Vendor { get; }
    string Version { get; }
    /// <summary>Sets the window's graphics API (OpenGL context, Vulkan surface or none for Direct3D).</summary>
    WindowOptions ConfigureWindow(WindowOptions options);
    /// <param name="window">Null when running headless (no window, no swap chain).</param>
    /// <param name="software">Use the API's software renderer (WARP / lavapipe) — for automated testing only.</param>
    void Init(IWindow? window, int width, int height, bool software);
    void CreateScenes(IReadOnlyList<string> names);
    /// <summary>Starts a frame (acquires a swap-chain image, waits for the oldest frame in flight).</summary>
    void BeginFrame();
    /// <summary>Draws scene <paramref name="index"/> into the off-screen image.</summary>
    void RenderScene(int index, double time);
    /// <summary>Scales the off-screen image into the window (or clears it when <paramref name="showScene"/> is false), draws the progress bar and presents.</summary>
    void EndFrame(bool showScene, double overall, int sceneIndex, int sceneCount);
    /// <summary>Allocates up to <paramref name="targetBytes"/> of video memory for the VRAM check; returns what was allocated.</summary>
    long InitVram(long targetBytes);
    /// <summary>Writes and verifies the whole VRAM test area once.</summary>
    (long Errors, string? Detail) VramPass();
    /// <summary>Copies <paramref name="bytes"/> to the graphics card and back; returns GB/s each way.</summary>
    (double UploadGBs, double DownloadGBs) TransferTest(long bytes);
    /// <summary>BGRA pixels of the off-screen image, top row first.</summary>
    byte[] ReadPixels();
    void WaitIdle();
}

/// <summary>
/// The per-draw constants every API uses (one 256-byte block per draw). Layout matches the HLSL <c>Params</c> cbuffer.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct SceneParams
{
    public Matrix4x4 ViewProj;
    public Vector4 Eye;
    public float Time, Dt, Layer; public uint Base;
    public Vector2 Res; public uint Seed, Count;
    public Vector4 Rect;   // overlay / blit: x, y, w, h in 0..1
    public Vector4 Color;  // overlay colour
    public const int Stride = 256;
}

/// <summary>Scene settings shared by all APIs, so each one does exactly the same work.</summary>
internal static class SceneMath
{
    public const int GeometryInstances = 24000, GeometryBatches = 6, SphereSegments = 40, SphereRings = 24;
    public const int ShadingBands = 8;
    public const int Particles = 1 << 20, ComputeSteps = 4;
    public const int FillLayers = 40, FillTextureSize = 2048;

    public static SceneParams Geometry(double time, int w, int h, int batch)
    {
        float t = (float)time;
        var eye = new Vector3(MathF.Cos(t * 0.2f) * 70, 25 + MathF.Sin(t * 0.3f) * 10, MathF.Sin(t * 0.2f) * 70);
        var vp = Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.UnitY) * Matrix4x4.CreatePerspectiveFieldOfView(1.0f, w / (float)h, 0.5f, 400f);
        return new SceneParams { ViewProj = vp, Eye = new Vector4(eye, 0), Time = t, Base = (uint)(batch * (GeometryInstances / GeometryBatches)), Res = new Vector2(w, h) };
    }

    public static SceneParams Shading(double time, int w, int h) => new() { Time = (float)time, Res = new Vector2(w, h) };

    public static SceneParams Compute(double time, int w, int h)
    {
        float t = (float)time;
        var eye = new Vector3(MathF.Cos(t * 0.15f) * 32, 12, MathF.Sin(t * 0.15f) * 32);
        var vp = Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.UnitY) * Matrix4x4.CreatePerspectiveFieldOfView(1.0f, w / (float)h, 0.1f, 200f);
        return new SceneParams { ViewProj = vp, Time = t, Dt = 0.008f, Res = new Vector2(w, h), Count = Particles };
    }

    public static SceneParams Fill(double time, int w, int h, int layer) => new() { Time = (float)time, Layer = layer, Res = new Vector2(w, h) };

    /// <summary>The overlay progress bar as rectangles (x, y, w, h in 0..1 of the window, y from the top) and colours.</summary>
    public static List<(Vector4 Rect, Vector4 Color)> Overlay(double overall, int scene, int scenes, int windowHeight)
    {
        float bar = MathF.Max(6, windowHeight / 90f) / Math.Max(1, windowHeight);
        var list = new List<(Vector4, Vector4)>
        {
            (new Vector4(0, 1 - bar, 1, bar), new Vector4(0.08f, 0.09f, 0.11f, 1)),
            (new Vector4(0, 1 - bar, (float)Math.Clamp(overall, 0, 1), bar), new Vector4(1f, 0.48f, 0.24f, 1)),
        };
        for (int i = 1; i < scenes; i++) list.Add((new Vector4(i / (float)scenes - 0.001f, 1 - bar, 0.002f, bar), new Vector4(0, 0, 0, 1)));
        return list;
    }

    /// <summary>Initial particle state: (pos.xyzw, vel.xyzw) × <see cref="Particles"/>.</summary>
    public static float[] ParticleData()
    {
        var data = new float[Particles * 8];
        var rng = new Random(7);
        for (int i = 0; i < Particles; i++)
        {
            double th = rng.NextDouble() * Math.PI * 2, r = 4 + rng.NextDouble() * 10;
            data[i * 8] = (float)(Math.Cos(th) * r); data[i * 8 + 1] = (float)(rng.NextDouble() * 4 - 2); data[i * 8 + 2] = (float)(Math.Sin(th) * r); data[i * 8 + 3] = 1;
            data[i * 8 + 4] = (float)(-Math.Sin(th) * 3); data[i * 8 + 6] = (float)(Math.Cos(th) * 3);
        }
        return data;
    }

    /// <summary>UV sphere: positions (xyz) and triangle indices.</summary>
    public static (float[] Vertices, uint[] Indices) Sphere()
    {
        var verts = new List<float>();
        var idx = new List<uint>();
        for (int r = 0; r <= SphereRings; r++)
        {
            double phi = Math.PI * r / SphereRings;
            for (int s = 0; s <= SphereSegments; s++)
            {
                double th = 2 * Math.PI * s / SphereSegments;
                verts.Add((float)(Math.Sin(phi) * Math.Cos(th))); verts.Add((float)Math.Cos(phi)); verts.Add((float)(Math.Sin(phi) * Math.Sin(th)));
            }
        }
        for (int r = 0; r < SphereRings; r++)
            for (int s = 0; s < SphereSegments; s++)
            {
                uint a = (uint)(r * (SphereSegments + 1) + s), b = a + SphereSegments + 1;
                idx.AddRange(new[] { a, b, a + 1, b, b + 1, a + 1 });
            }
        return (verts.ToArray(), idx.ToArray());
    }

    /// <summary>The 2048² procedural RGBA texture used by the fill-rate scene.</summary>
    public static uint[] FillTexture()
    {
        const int size = FillTextureSize;
        var pixels = new uint[size * size];
        Parallel.For(0, size, y =>
        {
            uint rs = (uint)(y * 2654435761u) | 1;
            for (int x = 0; x < size; x++)
            {
                double fx = x / (double)size * Math.PI * 2, fy = y / (double)size * Math.PI * 2;
                double plasma = Math.Sin(fx * 3 + Math.Sin(fy * 2) * 2) + Math.Sin(fy * 5 + Math.Cos(fx * 3)) + Math.Sin((fx + fy) * 4);
                double rings = Math.Sin(Math.Sqrt(Math.Pow(Math.Sin(fx * 4), 2) + Math.Pow(Math.Sin(fy * 4), 2)) * 18);
                double v = 0.5 + 0.25 * plasma / 3 + 0.25 * rings;
                rs ^= rs << 13; rs ^= rs >> 17; rs ^= rs << 5;
                double grain = (rs & 0xFF) / 255.0 * 0.15;
                byte r = (byte)Math.Clamp((v * 0.9 + grain) * 255, 0, 255);
                byte g = (byte)Math.Clamp((0.5 + 0.5 * Math.Sin(v * 6 + 2) + grain) * 200, 0, 255);
                byte b = (byte)Math.Clamp((1 - v + grain) * 255, 0, 255);
                pixels[y * size + x] = 0xFF000000u | ((uint)b << 16) | ((uint)g << 8) | r;
            }
        });
        return pixels;
    }

    /// <summary>Full mip chain of an RGBA8 image (box filter), largest first.</summary>
    public static List<uint[]> Mips(uint[] level0, int size)
    {
        var list = new List<uint[]> { level0 };
        var cur = level0; int s = size;
        while (s > 1)
        {
            int n = s / 2;
            var next = new uint[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    uint a = cur[2 * y * s + 2 * x], b = cur[2 * y * s + 2 * x + 1], c = cur[(2 * y + 1) * s + 2 * x], d = cur[(2 * y + 1) * s + 2 * x + 1];
                    uint px = 0;
                    for (int ch = 0; ch < 32; ch += 8)
                        px |= ((((a >> ch) & 0xFF) + ((b >> ch) & 0xFF) + ((c >> ch) & 0xFF) + ((d >> ch) & 0xFF) + 2) / 4) << ch;
                    next[y * n + x] = px;
                }
            list.Add(next); cur = next; s = n;
        }
        return list;
    }

    /// <summary>Writes BGRA top-down pixels as a .bmp file.</summary>
    public static void SaveBmp(string path, byte[] bgraTopDown, int w, int h)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        int size = w * h * 4;
        bw.Write((byte)'B'); bw.Write((byte)'M'); bw.Write(54 + size); bw.Write(0); bw.Write(54);
        bw.Write(40); bw.Write(w); bw.Write(-h); bw.Write((short)1); bw.Write((short)32); bw.Write(0); bw.Write(size); bw.Write(2835); bw.Write(2835); bw.Write(0); bw.Write(0);
        bw.Write(bgraTopDown, 0, size);
    }
}
