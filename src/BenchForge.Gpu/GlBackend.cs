using System.Diagnostics;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace BenchForge.Gpu;

/// <summary>OpenGL 4.3 core — the original BenchForge 3D test.</summary>
internal sealed unsafe class GlBackend : IGpuBackend
{
    private IWindow _window = null!;
    private GL _gl = null!;
    private uint _fbo, _colorTex, _depthRb;
    private int _w, _h;
    private readonly List<IScene> _scenes = new();
    private VramChecker? _vram;

    public string Api => Core.Gpu.GpuApis.OpenGL;
    public string Renderer { get; private set; } = "";
    public string Vendor { get; private set; } = "";
    public string Version { get; private set; } = "";

    public WindowOptions ConfigureWindow(WindowOptions o) => o with
    {
        API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(4, 3)),
        ShouldSwapAutomatically = false,
    };

    public void Init(IWindow? window, int width, int height, bool software)
    {
        _window = window ?? throw new InvalidOperationException("OpenGL needs a window (it can be hidden).");
        _gl = window.CreateOpenGL();
        _w = width; _h = height;
        Renderer = _gl.GetStringS(StringName.Renderer);
        Vendor = _gl.GetStringS(StringName.Vendor);
        Version = "OpenGL " + _gl.GetStringS(StringName.Version);
        CreateTarget();
    }

    public void CreateScenes(IReadOnlyList<string> names)
    {
        foreach (var name in names)
        {
            IScene s = name switch
            {
                Core.Gpu.GpuScenes.Geometry => new GeometryScene(),
                Core.Gpu.GpuScenes.Shading => new ShadingScene(),
                Core.Gpu.GpuScenes.Compute => new ComputeScene(),
                _ => new FillScene(),
            };
            s.Init(_gl, _w, _h);
            _scenes.Add(s);
        }
    }

    public void BeginFrame() { }

    public void RenderScene(int index, double time)
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        _gl.Viewport(0, 0, (uint)_w, (uint)_h);
        _scenes[index].Render(_gl, time, _w, _h);
    }

    public void EndFrame(bool showScene, double overall, int sceneIndex, int sceneCount)
    {
        var fb = _window.FramebufferSize;
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.Viewport(0, 0, (uint)fb.X, (uint)fb.Y);
        if (showScene)
        {
            _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _fbo);
            _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, 0);
            _gl.BlitFramebuffer(0, 0, _w, _h, 0, 0, fb.X, fb.Y, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Linear);
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        }
        else
        {
            _gl.ClearColor(0, 0, 0, 1);
            _gl.Clear(ClearBufferMask.ColorBufferBit);
        }
        // Progress bar with scissored clears (no text rendering needed).
        _gl.Enable(EnableCap.ScissorTest);
        foreach (var (r, c) in SceneMath.Overlay(overall, sceneIndex, sceneCount, fb.Y))
        {
            int x = (int)(r.X * fb.X), w = Math.Max(1, (int)(r.Z * fb.X)), hh = Math.Max(1, (int)(r.W * fb.Y));
            int y = fb.Y - (int)(r.Y * fb.Y) - hh; // GL origin is bottom-left
            _gl.Scissor(x, Math.Max(0, y), (uint)w, (uint)hh);
            _gl.ClearColor(c.X, c.Y, c.Z, c.W);
            _gl.Clear(ClearBufferMask.ColorBufferBit);
        }
        _gl.Disable(EnableCap.ScissorTest);
        _window.SwapBuffers();
    }

    public long InitVram(long targetBytes)
    {
        _vram = new VramChecker();
        _vram.Init(_gl, targetBytes);
        return _vram.BytesPerPass;
    }

    public (long Errors, string? Detail) VramPass() => _vram == null ? (0, null) : _vram.RunPass(_gl);

    public (double UploadGBs, double DownloadGBs) TransferTest(long bytes)
    {
        var data = new byte[bytes];
        new Random(3).NextBytes(data);
        uint buf = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.CopyWriteBuffer, buf);
        _gl.BufferData(BufferTargetARB.CopyWriteBuffer, (nuint)bytes, null, BufferUsageARB.StreamCopy);
        _gl.Finish();
        double up = 0, down = 0;
        fixed (byte* p = data)
        {
            for (int pass = 0; pass < 3; pass++)
            {
                var sw = Stopwatch.StartNew();
                _gl.BufferSubData(BufferTargetARB.CopyWriteBuffer, 0, (nuint)bytes, p);
                _gl.Finish();
                up = Math.Max(up, bytes / sw.Elapsed.TotalSeconds / 1e9);
                sw.Restart();
                _gl.GetBufferSubData(BufferTargetARB.CopyWriteBuffer, 0, (nuint)bytes, p);
                down = Math.Max(down, bytes / sw.Elapsed.TotalSeconds / 1e9);
            }
        }
        _gl.DeleteBuffer(buf);
        return (up, down);
    }

    public byte[] ReadPixels()
    {
        var px = new byte[_w * _h * 4];
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _fbo);
        fixed (byte* p = px) _gl.ReadPixels(0, 0, (uint)_w, (uint)_h, PixelFormat.Bgra, PixelType.UnsignedByte, p);
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
        // GL rows are bottom-up.
        var flipped = new byte[px.Length];
        int row = _w * 4;
        for (int y = 0; y < _h; y++) System.Buffer.BlockCopy(px, y * row, flipped, (_h - 1 - y) * row, row);
        return flipped;
    }

    public void WaitIdle() => _gl.Finish();

    private void CreateTarget()
    {
        _fbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        _colorTex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _colorTex);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)_w, (uint)_h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _colorTex, 0);
        _depthRb = _gl.GenRenderbuffer();
        _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depthRb);
        _gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.Depth24Stencil8, (uint)_w, (uint)_h);
        _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthStencilAttachment, RenderbufferTarget.Renderbuffer, _depthRb);
        var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete) throw new InvalidOperationException("Framebuffer incomplete: " + status);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    public void Dispose()
    {
        foreach (var s in _scenes) s.Dispose();
    }
}
