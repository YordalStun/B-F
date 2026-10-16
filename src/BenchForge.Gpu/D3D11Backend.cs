using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using D3DBuffer = Silk.NET.Direct3D11.ID3D11Buffer;

namespace BenchForge.Gpu;

/// <summary>Direct3D 11 backend (feature level 11.0): the same scenes as OpenGL, from the shared HLSL.</summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class D3D11Backend : IGpuBackend
{
    private D3D11 _d3d = null!;
    private DXGI _dxgi = null!;
    private ID3D11Device* _dev;
    private ID3D11DeviceContext* _ctx;
    private IDXGISwapChain1* _swap;
    private ID3D11RenderTargetView* _backRtv;
    private int _backW, _backH;
    private IWindow? _window;
    private int _w, _h;

    // Off-screen target
    private ID3D11Texture2D* _color, _depthTex, _readback;
    private ID3D11RenderTargetView* _colorRtv;
    private ID3D11ShaderResourceView* _colorSrv;
    private ID3D11DepthStencilView* _dsv;

    // Shared state
    private D3DBuffer* _cb;
    private ID3D11SamplerState* _sampler;
    private ID3D11BlendState* _blendNone, _blendAdd, _blendAlpha;
    private ID3D11DepthStencilState* _depthOn, _depthOff;
    private ID3D11RasterizerState* _raster;
    private ID3D11Query* _event;

    // Shaders
    private ID3D11VertexShader* _fullVs, _geoVs, _partVs, _rectVs;
    private ID3D11PixelShader* _geoPs, _shadePs, _partPs, _fillPs, _blitPs, _rectPs;
    private ID3D11ComputeShader* _simCs, _vramWriteCs, _vramVerifyCs;
    private ID3D11InputLayout* _geoLayout;

    // Scenes
    private readonly List<string> _sceneNames = new();
    private D3DBuffer* _sphereVb, _sphereIb, _particles;
    private uint _sphereIndexCount;
    private ID3D11ShaderResourceView* _particlesSrv, _fillSrv;
    private ID3D11UnorderedAccessView* _particlesUav;
    private ID3D11Texture2D* _fillTex;

    // VRAM check
    private readonly List<(nint Buf, nint Uav, long Words)> _vram = new();
    private D3DBuffer* _errBuf, _errReadback;
    private ID3D11UnorderedAccessView* _errUav;
    private uint _vramPass;

    public string Api => Core.Gpu.GpuApis.D3D11;
    public string Renderer { get; private set; } = "";
    public string Vendor { get; private set; } = "";
    public string Version { get; private set; } = "Direct3D 11";

    public WindowOptions ConfigureWindow(WindowOptions o) => o with { API = GraphicsAPI.None };

    public void Init(IWindow? window, int width, int height, bool software)
    {
        _window = window; _w = width; _h = height;
        _d3d = D3D11.GetApi(null, false);
        D3DCommon.RemovedReason = () => _dev != null ? _dev->GetDeviceRemovedReason() : 0;
        _dxgi = DXGI.GetApi(null, false);
        D3DFeatureLevel level = D3DFeatureLevel.Level110, got;
        ID3D11Device* dev = null; ID3D11DeviceContext* ctx = null;
        uint flags = (uint)CreateDeviceFlag.BgraSupport;
        int hr = _d3d.CreateDevice(null, software ? D3DDriverType.Warp : D3DDriverType.Hardware, 0, flags, &level, 1, D3D11.SdkVersion, &dev, &got, &ctx);
        D3DCommon.Check(hr, "Direct3D 11 device (feature level 11.0)");
        _dev = dev; _ctx = ctx;

        // Adapter name via DXGI.
        IDXGIDevice* dxgiDev = null;
        var gDev = IDXGIDevice.Guid;
        if (_dev->QueryInterface(&gDev, (void**)&dxgiDev) >= 0)
        {
            IDXGIAdapter* adapter = null;
            if (dxgiDev->GetAdapter(&adapter) >= 0)
            {
                AdapterDesc desc;
                adapter->GetDesc(&desc);
                Renderer = new string((char*)desc.Description).TrimEnd('\0');
                Vendor = desc.VendorId switch { 0x10DE => "NVIDIA", 0x1002 => "AMD", 0x8086 => "Intel", 0x1414 => "Microsoft", _ => $"Vendor 0x{desc.VendorId:X4}" };
                Version = $"Direct3D 11 (feature level {((int)got >> 12) & 0xF}.{((int)got >> 8) & 0xF}), {desc.DedicatedVideoMemory / (1024 * 1024)} MB dedicated video memory";
                adapter->Release();
            }
            dxgiDev->Release();
        }

        CreateOffscreen();
        CreateStates();
        if (window != null) CreateSwapchain();
    }

    private void CreateOffscreen()
    {
        var td = new Texture2DDesc
        {
            Width = (uint)_w, Height = (uint)_h, MipLevels = 1, ArraySize = 1, Format = Format.FormatR8G8B8A8Unorm, SampleDesc = new SampleDesc(1, 0),
            Usage = Usage.Default, BindFlags = (uint)(BindFlag.RenderTarget | BindFlag.ShaderResource),
        };
        ID3D11Texture2D* t;
        D3DCommon.Check(_dev->CreateTexture2D(&td, null, &t), "off-screen image"); _color = t;
        ID3D11RenderTargetView* rtv; D3DCommon.Check(_dev->CreateRenderTargetView((ID3D11Resource*)_color, null, &rtv), "RTV"); _colorRtv = rtv;
        ID3D11ShaderResourceView* srv; D3DCommon.Check(_dev->CreateShaderResourceView((ID3D11Resource*)_color, null, &srv), "SRV"); _colorSrv = srv;
        var dd = td with { Format = Format.FormatD32Float, BindFlags = (uint)BindFlag.DepthStencil };
        D3DCommon.Check(_dev->CreateTexture2D(&dd, null, &t), "depth"); _depthTex = t;
        ID3D11DepthStencilView* dsv; D3DCommon.Check(_dev->CreateDepthStencilView((ID3D11Resource*)_depthTex, null, &dsv), "DSV"); _dsv = dsv;
        var rd = td with { Usage = Usage.Staging, BindFlags = 0, CPUAccessFlags = (uint)CpuAccessFlag.Read };
        D3DCommon.Check(_dev->CreateTexture2D(&rd, null, &t), "readback"); _readback = t;
    }

    private void CreateStates()
    {
        var cbd = new BufferDesc { ByteWidth = SceneParams.Stride, Usage = Usage.Dynamic, BindFlags = (uint)BindFlag.ConstantBuffer, CPUAccessFlags = (uint)CpuAccessFlag.Write };
        D3DBuffer* cb; D3DCommon.Check(_dev->CreateBuffer(&cbd, null, &cb), "constant buffer"); _cb = cb;

        var sd = new SamplerDesc { Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Wrap, AddressV = TextureAddressMode.Wrap, AddressW = TextureAddressMode.Wrap, MaxLOD = float.MaxValue, ComparisonFunc = ComparisonFunc.Never };
        ID3D11SamplerState* s; D3DCommon.Check(_dev->CreateSamplerState(&sd, &s), "sampler"); _sampler = s;

        _blendNone = Blend(false, Silk.NET.Direct3D11.Blend.One, Silk.NET.Direct3D11.Blend.Zero);
        _blendAdd = Blend(true, Silk.NET.Direct3D11.Blend.One, Silk.NET.Direct3D11.Blend.One);
        _blendAlpha = Blend(true, Silk.NET.Direct3D11.Blend.SrcAlpha, Silk.NET.Direct3D11.Blend.InvSrcAlpha);

        var dsOn = new DepthStencilDesc { DepthEnable = 1, DepthWriteMask = DepthWriteMask.All, DepthFunc = ComparisonFunc.Less };
        ID3D11DepthStencilState* ds; D3DCommon.Check(_dev->CreateDepthStencilState(&dsOn, &ds), "depth state"); _depthOn = ds;
        var dsOff = new DepthStencilDesc { DepthEnable = 0, DepthWriteMask = DepthWriteMask.Zero, DepthFunc = ComparisonFunc.Always };
        D3DCommon.Check(_dev->CreateDepthStencilState(&dsOff, &ds), "depth state"); _depthOff = ds;
        var rs = new RasterizerDesc { FillMode = FillMode.Solid, CullMode = CullMode.None, DepthClipEnable = 1, ScissorEnable = 1 };
        ID3D11RasterizerState* r; D3DCommon.Check(_dev->CreateRasterizerState(&rs, &r), "rasterizer"); _raster = r;
        var qd = new QueryDesc { Query = Query.Event };
        ID3D11Query* q; D3DCommon.Check(_dev->CreateQuery(&qd, &q), "query"); _event = q;

        _fullVs = Vs("FullVS"); _rectVs = Vs("RectVS"); _partVs = Vs("PartVS");
        var geoCode = D3DCommon.Compile("GeoVS", "vs_5_0");
        fixed (byte* p = geoCode)
        {
            ID3D11VertexShader* v; D3DCommon.Check(_dev->CreateVertexShader(p, (nuint)geoCode.Length, null, &v), "GeoVS"); _geoVs = v;
            var sem = (byte*)SilkMarshal.StringToPtr("POSITION");
            var el = new InputElementDesc { SemanticName = sem, SemanticIndex = 0, Format = Format.FormatR32G32B32Float, InputSlot = 0, AlignedByteOffset = 0, InputSlotClass = InputClassification.PerVertexData };
            ID3D11InputLayout* il; D3DCommon.Check(_dev->CreateInputLayout(&el, 1, p, (nuint)geoCode.Length, &il), "input layout"); _geoLayout = il;
            SilkMarshal.Free((nint)sem);
        }
        _geoPs = Ps("GeoPS"); _shadePs = Ps("ShadePS"); _partPs = Ps("PartPS"); _fillPs = Ps("FillPS"); _blitPs = Ps("BlitPS"); _rectPs = Ps("RectPS");
        _simCs = Cs("SimCS"); _vramWriteCs = Cs("VramWriteCS"); _vramVerifyCs = Cs("VramVerifyCS");
    }

    private ID3D11BlendState* Blend(bool on, Silk.NET.Direct3D11.Blend src, Silk.NET.Direct3D11.Blend dst)
    {
        var bd = new BlendDesc();
        bd.RenderTarget[0] = new RenderTargetBlendDesc
        {
            BlendEnable = on, SrcBlend = src, DestBlend = dst, BlendOp = BlendOp.Add,
            SrcBlendAlpha = src, DestBlendAlpha = dst, BlendOpAlpha = BlendOp.Add, RenderTargetWriteMask = (byte)ColorWriteEnable.All,
        };
        ID3D11BlendState* b; D3DCommon.Check(_dev->CreateBlendState(&bd, &b), "blend state");
        return b;
    }

    private ID3D11VertexShader* Vs(string e) { var c = D3DCommon.Compile(e, "vs_5_0"); fixed (byte* p = c) { ID3D11VertexShader* s; D3DCommon.Check(_dev->CreateVertexShader(p, (nuint)c.Length, null, &s), e); return s; } }
    private ID3D11PixelShader* Ps(string e) { var c = D3DCommon.Compile(e, "ps_5_0"); fixed (byte* p = c) { ID3D11PixelShader* s; D3DCommon.Check(_dev->CreatePixelShader(p, (nuint)c.Length, null, &s), e); return s; } }
    private ID3D11ComputeShader* Cs(string e) { var c = D3DCommon.Compile(e, "cs_5_0"); fixed (byte* p = c) { ID3D11ComputeShader* s; D3DCommon.Check(_dev->CreateComputeShader(p, (nuint)c.Length, null, &s), e); return s; } }

    private void CreateSwapchain()
    {
        IDXGIFactory2* factory = null;
        var g = IDXGIFactory2.Guid;
        D3DCommon.Check(_dxgi.CreateDXGIFactory2(0, &g, (void**)&factory), "DXGI factory");
        var fb = _window!.FramebufferSize;
        _backW = Math.Max(1, fb.X); _backH = Math.Max(1, fb.Y);
        var sd = new SwapChainDesc1
        {
            Width = (uint)_backW, Height = (uint)_backH, Format = Format.FormatB8G8R8A8Unorm, SampleDesc = new SampleDesc(1, 0),
            BufferUsage = DXGI.UsageRenderTargetOutput, BufferCount = 2, SwapEffect = SwapEffect.FlipDiscard, Scaling = Scaling.Stretch, AlphaMode = AlphaMode.Ignore,
        };
        IDXGISwapChain1* sc;
        D3DCommon.Check(factory->CreateSwapChainForHwnd((IUnknown*)_dev, _window.Native!.Win32!.Value.Hwnd, &sd, null, null, &sc), "swap chain");
        _swap = sc;
        factory->Release();
        CreateBackRtv();
    }

    private void CreateBackRtv()
    {
        ID3D11Texture2D* back;
        var g = ID3D11Texture2D.Guid;
        D3DCommon.Check(_swap->GetBuffer(0, &g, (void**)&back), "back buffer");
        ID3D11RenderTargetView* rtv;
        D3DCommon.Check(_dev->CreateRenderTargetView((ID3D11Resource*)back, null, &rtv), "back buffer RTV");
        _backRtv = rtv;
        back->Release();
    }

    // ------------------------------------------------------------------ scenes

    public void CreateScenes(IReadOnlyList<string> names)
    {
        _sceneNames.AddRange(names);
        var (verts, idx) = SceneMath.Sphere();
        _sphereVb = InitBuffer(MemoryMarshal.AsBytes(verts.AsSpan()), BindFlag.VertexBuffer);
        _sphereIb = InitBuffer(MemoryMarshal.AsBytes(idx.AsSpan()), BindFlag.IndexBuffer);
        _sphereIndexCount = (uint)idx.Length;

        var pdata = SceneMath.ParticleData();
        _particles = InitBuffer(MemoryMarshal.AsBytes(pdata.AsSpan()), BindFlag.ShaderResource | BindFlag.UnorderedAccess, structStride: 32);
        _particlesSrv = BufferSrv(_particles, SceneMath.Particles);
        _particlesUav = BufferUav(_particles, SceneMath.Particles);

        var mips = SceneMath.Mips(SceneMath.FillTexture(), SceneMath.FillTextureSize);
        var td = new Texture2DDesc
        {
            Width = SceneMath.FillTextureSize, Height = SceneMath.FillTextureSize, MipLevels = (uint)mips.Count, ArraySize = 1, Format = Format.FormatR8G8B8A8Unorm,
            SampleDesc = new SampleDesc(1, 0), Usage = Usage.Immutable, BindFlags = (uint)BindFlag.ShaderResource,
        };
        var init = new SubresourceData[mips.Count];
        var handles = mips.Select(m => GCHandle.Alloc(m, GCHandleType.Pinned)).ToArray();
        try
        {
            int s = SceneMath.FillTextureSize;
            for (int i = 0; i < mips.Count; i++) { init[i] = new SubresourceData(handles[i].AddrOfPinnedObject().ToPointer(), (uint)(s * 4), 0); s = Math.Max(1, s / 2); }
            ID3D11Texture2D* tex;
            fixed (SubresourceData* p = init) D3DCommon.Check(_dev->CreateTexture2D(&td, p, &tex), "fill texture");
            _fillTex = tex;
        }
        finally { foreach (var h in handles) h.Free(); }
        ID3D11ShaderResourceView* srv; D3DCommon.Check(_dev->CreateShaderResourceView((ID3D11Resource*)_fillTex, null, &srv), "fill SRV"); _fillSrv = srv;
    }

    private D3DBuffer* InitBuffer(ReadOnlySpan<byte> data, BindFlag bind, uint structStride = 0)
    {
        var bd = new BufferDesc
        {
            ByteWidth = (uint)data.Length, Usage = Usage.Default, BindFlags = (uint)bind,
            MiscFlags = structStride > 0 ? (uint)ResourceMiscFlag.BufferStructured : 0, StructureByteStride = structStride,
        };
        D3DBuffer* b;
        fixed (byte* p = data)
        {
            var init = new SubresourceData(p, 0, 0);
            D3DCommon.Check(_dev->CreateBuffer(&bd, &init, &b), "buffer");
        }
        return b;
    }

    private ID3D11ShaderResourceView* BufferSrv(D3DBuffer* b, int elements)
    {
        var d = new ShaderResourceViewDesc { Format = Format.FormatUnknown, ViewDimension = D3DSrvDimension.D3DSrvDimensionBuffer };
        d.Buffer.FirstElement = 0; d.Buffer.NumElements = (uint)elements;
        ID3D11ShaderResourceView* v; D3DCommon.Check(_dev->CreateShaderResourceView((ID3D11Resource*)b, &d, &v), "buffer SRV");
        return v;
    }

    private ID3D11UnorderedAccessView* BufferUav(D3DBuffer* b, long elements)
    {
        var d = new UnorderedAccessViewDesc { Format = Format.FormatUnknown, ViewDimension = UavDimension.Buffer };
        d.Buffer.FirstElement = 0; d.Buffer.NumElements = (uint)elements;
        ID3D11UnorderedAccessView* v; D3DCommon.Check(_dev->CreateUnorderedAccessView((ID3D11Resource*)b, &d, &v), "buffer UAV");
        return v;
    }

    // ------------------------------------------------------------------ frames

    public void BeginFrame()
    {
        if (_swap == null) return;
        var fb = _window!.FramebufferSize;
        if (fb.X > 0 && fb.Y > 0 && (fb.X != _backW || fb.Y != _backH))
        {
            _backRtv->Release(); _backRtv = null;
            _ctx->OMSetRenderTargets(0, null, null);
            D3DCommon.Check(_swap->ResizeBuffers(0, (uint)fb.X, (uint)fb.Y, Format.FormatUnknown, 0), "resize");
            _backW = fb.X; _backH = fb.Y;
            CreateBackRtv();
        }
    }

    private void SetParams(in SceneParams p)
    {
        MappedSubresource m;
        D3DCommon.Check(_ctx->Map((ID3D11Resource*)_cb, 0, Map.WriteDiscard, 0, &m), "map constants");
        Unsafe.Write(m.PData, p);
        _ctx->Unmap((ID3D11Resource*)_cb, 0);
    }

    private void Bind(ID3D11VertexShader* vs, ID3D11PixelShader* ps, ID3D11BlendState* blend, ID3D11DepthStencilState* depth)
    {
        _ctx->VSSetShader(vs, null, 0);
        _ctx->PSSetShader(ps, null, 0);
        _ctx->OMSetBlendState(blend, null, 0xFFFFFFFF);
        _ctx->OMSetDepthStencilState(depth, 0);
        var cb = _cb;
        _ctx->VSSetConstantBuffers(0, 1, &cb);
        _ctx->PSSetConstantBuffers(0, 1, &cb);
    }

    private void SetTarget(ID3D11RenderTargetView* rtv, ID3D11DepthStencilView* dsv, int w, int h)
    {
        _ctx->OMSetRenderTargets(1, &rtv, dsv);
        var vp = new Viewport(0, 0, w, h, 0, 1);
        _ctx->RSSetViewports(1, &vp);
        var sc = new Box2D<int>(0, 0, w, h);
        _ctx->RSSetScissorRects(1, &sc);
        _ctx->RSSetState(_raster);
    }

    public void RenderScene(int index, double time)
    {
        string name = _sceneNames[index];
        var cb = _cb;
        if (name == Core.Gpu.GpuScenes.Compute)
        {
            // The particle buffer can't be a UAV and an SRV at the same time: unbind the vertex-shader view first.
            ID3D11ShaderResourceView* nullSrv = null;
            _ctx->VSSetShaderResources(1, 1, &nullSrv);
            SetParams(SceneMath.Compute(time, _w, _h));
            _ctx->CSSetShader(_simCs, null, 0);
            _ctx->CSSetConstantBuffers(0, 1, &cb);
            var uav = _particlesUav;
            _ctx->CSSetUnorderedAccessViews(0, 1, &uav, null);
            for (int s = 0; s < SceneMath.ComputeSteps; s++) _ctx->Dispatch(SceneMath.Particles / 256, 1, 1);
            ID3D11UnorderedAccessView* nullUav = null;
            _ctx->CSSetUnorderedAccessViews(0, 1, &nullUav, null);
        }

        SetTarget(_colorRtv, _dsv, _w, _h);
        var clear = name switch
        {
            Core.Gpu.GpuScenes.Geometry => stackalloc float[] { 0.02f, 0.03f, 0.05f, 1 },
            Core.Gpu.GpuScenes.Compute => stackalloc float[] { 0, 0, 0.01f, 1 },
            _ => stackalloc float[] { 0, 0, 0, 1 },
        };
        fixed (float* c = clear) _ctx->ClearRenderTargetView(_colorRtv, c);
        _ctx->ClearDepthStencilView(_dsv, (uint)ClearFlag.Depth, 1, 0);
        _ctx->IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);

        switch (name)
        {
            case Core.Gpu.GpuScenes.Geometry:
            {
                Bind(_geoVs, _geoPs, _blendNone, _depthOn);
                _ctx->IASetInputLayout(_geoLayout);
                uint stride = 12, offset = 0; var vb = _sphereVb;
                _ctx->IASetVertexBuffers(0, 1, &vb, &stride, &offset);
                _ctx->IASetIndexBuffer(_sphereIb, Format.FormatR32Uint, 0);
                for (int b = 0; b < SceneMath.GeometryBatches; b++)
                {
                    SetParams(SceneMath.Geometry(time, _w, _h, b));
                    _ctx->DrawIndexedInstanced(_sphereIndexCount, SceneMath.GeometryInstances / SceneMath.GeometryBatches, 0, 0, 0);
                    _ctx->Flush(); // smaller submissions keep slow GPUs clear of the driver timeout
                }
                _ctx->IASetInputLayout(null);
                break;
            }
            case Core.Gpu.GpuScenes.Shading:
            {
                Bind(_fullVs, _shadePs, _blendNone, _depthOff);
                SetParams(SceneMath.Shading(time, _w, _h));
                for (int i = 0; i < SceneMath.ShadingBands; i++)
                {
                    int y0 = _h * i / SceneMath.ShadingBands, y1 = _h * (i + 1) / SceneMath.ShadingBands;
                    var band = new Box2D<int>(0, y0, _w, y1);
                    _ctx->RSSetScissorRects(1, &band);
                    _ctx->Draw(3, 0);
                    _ctx->Flush();
                }
                break;
            }
            case Core.Gpu.GpuScenes.Compute:
            {
                Bind(_partVs, _partPs, _blendAdd, _depthOff);
                SetParams(SceneMath.Compute(time, _w, _h));
                var srv = _particlesSrv;
                _ctx->VSSetShaderResources(1, 1, &srv);
                _ctx->Draw(SceneMath.Particles * 6, 0);
                break;
            }
            default:
            {
                Bind(_fullVs, _fillPs, _blendAlpha, _depthOff);
                var srv = _fillSrv; var smp = _sampler;
                _ctx->PSSetShaderResources(0, 1, &srv);
                _ctx->PSSetSamplers(0, 1, &smp);
                for (int layer = 0; layer < SceneMath.FillLayers; layer++)
                {
                    SetParams(SceneMath.Fill(time, _w, _h, layer));
                    _ctx->Draw(3, 0);
                }
                break;
            }
        }
    }

    public void EndFrame(bool showScene, double overall, int sceneIndex, int sceneCount)
    {
        if (_swap == null) { _ctx->Flush(); return; }
        SetTarget(_backRtv, null, _backW, _backH);
        var black = stackalloc float[] { 0, 0, 0, 1 };
        _ctx->ClearRenderTargetView(_backRtv, black);
        _ctx->IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
        if (showScene)
        {
            Bind(_fullVs, _blitPs, _blendNone, _depthOff);
            var srv = _colorSrv; var smp = _sampler;
            _ctx->PSSetShaderResources(0, 1, &srv);
            _ctx->PSSetSamplers(0, 1, &smp);
            _ctx->Draw(3, 0);
            ID3D11ShaderResourceView* nullSrv = null;
            _ctx->PSSetShaderResources(0, 1, &nullSrv);
        }
        Bind(_rectVs, _rectPs, _blendNone, _depthOff);
        foreach (var (r, c) in SceneMath.Overlay(overall, sceneIndex, sceneCount, _backH))
        {
            SetParams(new SceneParams { Rect = r, Color = c });
            _ctx->Draw(6, 0);
        }
        _swap->Present(0, 0);
    }

    // ------------------------------------------------------------------ VRAM, PCIe, screenshots

    public long InitVram(long targetBytes)
    {
        var ed = new BufferDesc { ByteWidth = 16, Usage = Usage.Default, BindFlags = (uint)BindFlag.UnorderedAccess, MiscFlags = (uint)ResourceMiscFlag.BufferStructured, StructureByteStride = 4 };
        D3DBuffer* e; D3DCommon.Check(_dev->CreateBuffer(&ed, null, &e), "error buffer"); _errBuf = e;
        _errUav = BufferUav(_errBuf, 4);
        var rd = new BufferDesc { ByteWidth = 16, Usage = Usage.Staging, CPUAccessFlags = (uint)CpuAccessFlag.Read };
        D3DCommon.Check(_dev->CreateBuffer(&rd, null, &e), "error readback"); _errReadback = e;
        long chunk = 128L << 20, total = 0;
        while (total < targetBytes)
        {
            long bytes = Math.Min(chunk, targetBytes - total);
            var bd = new BufferDesc { ByteWidth = (uint)bytes, Usage = Usage.Default, BindFlags = (uint)BindFlag.UnorderedAccess, MiscFlags = (uint)ResourceMiscFlag.BufferStructured, StructureByteStride = 4 };
            D3DBuffer* b;
            if (_dev->CreateBuffer(&bd, null, &b) < 0) break; // out of video memory: test what we got
            var uav = BufferUav(b, bytes / 4);
            _vram.Add(((nint)b, (nint)uav, bytes / 4));
            total += bytes;
        }
        return total;
    }

    public (long Errors, string? Detail) VramPass()
    {
        _vramPass++;
        uint seed = _vramPass * 2654435761u;
        var zero = stackalloc uint[4];
        _ctx->UpdateSubresource((ID3D11Resource*)_errBuf, 0, null, zero, 0, 0);
        var cb = _cb;
        _ctx->CSSetConstantBuffers(0, 1, &cb);
        var err = _errUav;
        _ctx->CSSetUnorderedAccessViews(2, 1, &err, null);
        foreach (var (buf, uavPtr, words) in _vram)
        {
            uint groups = (uint)Math.Min(65535, (words + 255) / 256);
            SetParams(new SceneParams { Seed = seed, Count = (uint)words, Base = groups * 256 });
            var uav = (ID3D11UnorderedAccessView*)uavPtr;
            _ctx->CSSetUnorderedAccessViews(1, 1, &uav, null);
            _ctx->CSSetShader(_vramWriteCs, null, 0);
            _ctx->Dispatch(groups, 1, 1);
            _ctx->CSSetShader(_vramVerifyCs, null, 0);
            _ctx->Dispatch(groups, 1, 1);
            _ctx->Flush();
        }
        ID3D11UnorderedAccessView* nul = null;
        _ctx->CSSetUnorderedAccessViews(1, 1, &nul, null);
        _ctx->CSSetUnorderedAccessViews(2, 1, &nul, null);
        _ctx->CopyResource((ID3D11Resource*)_errReadback, (ID3D11Resource*)_errBuf);
        MappedSubresource m;
        D3DCommon.Check(_ctx->Map((ID3D11Resource*)_errReadback, 0, Map.Read, 0, &m), "map errors");
        var r = new ReadOnlySpan<uint>(m.PData, 4).ToArray();
        _ctx->Unmap((ID3D11Resource*)_errReadback, 0);
        return r[0] == 0 ? (0, null) : (r[0], $"first bad word #{r[1]}: expected {r[2]:X8} read {r[3]:X8}");
    }

    public (double UploadGBs, double DownloadGBs) TransferTest(long bytes)
    {
        var data = new byte[bytes];
        new Random(3).NextBytes(data);
        var gd = new BufferDesc { ByteWidth = (uint)bytes, Usage = Usage.Default, BindFlags = (uint)BindFlag.ShaderResource };
        var sd = new BufferDesc { ByteWidth = (uint)bytes, Usage = Usage.Staging, CPUAccessFlags = (uint)CpuAccessFlag.Read };
        D3DBuffer* gpu, staging;
        D3DCommon.Check(_dev->CreateBuffer(&gd, null, &gpu), "transfer buffer");
        D3DCommon.Check(_dev->CreateBuffer(&sd, null, &staging), "staging buffer");
        double up = 0, down = 0;
        try
        {
            fixed (byte* p = data)
                for (int pass = 0; pass < 3; pass++)
                {
                    var sw = Stopwatch.StartNew();
                    _ctx->UpdateSubresource((ID3D11Resource*)gpu, 0, null, p, 0, 0);
                    WaitIdle();
                    up = Math.Max(up, bytes / sw.Elapsed.TotalSeconds / 1e9);
                    sw.Restart();
                    _ctx->CopyResource((ID3D11Resource*)staging, (ID3D11Resource*)gpu);
                    MappedSubresource m;
                    D3DCommon.Check(_ctx->Map((ID3D11Resource*)staging, 0, Map.Read, 0, &m), "map");
                    new ReadOnlySpan<byte>(m.PData, (int)bytes).CopyTo(data);
                    _ctx->Unmap((ID3D11Resource*)staging, 0);
                    down = Math.Max(down, bytes / sw.Elapsed.TotalSeconds / 1e9);
                }
        }
        finally { gpu->Release(); staging->Release(); }
        return (up, down);
    }

    public byte[] ReadPixels()
    {
        _ctx->CopyResource((ID3D11Resource*)_readback, (ID3D11Resource*)_color);
        MappedSubresource m;
        D3DCommon.Check(_ctx->Map((ID3D11Resource*)_readback, 0, Map.Read, 0, &m), "map readback");
        var px = new byte[_w * _h * 4];
        for (int y = 0; y < _h; y++)
            new ReadOnlySpan<byte>((byte*)m.PData + y * m.RowPitch, _w * 4).CopyTo(px.AsSpan(y * _w * 4));
        _ctx->Unmap((ID3D11Resource*)_readback, 0);
        for (int i = 0; i < px.Length; i += 4) (px[i], px[i + 2]) = (px[i + 2], px[i]); // RGBA → BGRA
        return px;
    }

    public void WaitIdle()
    {
        if (_ctx == null) return;
        _ctx->End((ID3D11Asynchronous*)_event);
        int done = 0;
        while (_ctx->GetData((ID3D11Asynchronous*)_event, &done, sizeof(int), 0) != 0) Thread.SpinWait(50);
    }

    public void Dispose()
    {
        if (_dev == null) return;
        foreach (var (b, u, _) in _vram) { ((ID3D11UnorderedAccessView*)u)->Release(); ((D3DBuffer*)b)->Release(); }
        if (_ctx != null) { _ctx->ClearState(); _ctx->Flush(); }
        if (_backRtv != null) _backRtv->Release();
        if (_swap != null) _swap->Release();
        _ctx->Release(); _dev->Release();
        _dev = null; _ctx = null;
    }
}
