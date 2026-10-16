using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using D3D12Range = Silk.NET.Direct3D12.Range;

namespace BenchForge.Gpu;

/// <summary>Direct3D 12 backend: the same scenes as OpenGL, from the shared HLSL (shader model 5.0).</summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class D3D12Backend : IGpuBackend
{
    private const int FramesInFlight = 2, MaxDrawsPerFrame = 64;
    private const Format ColorFormat = Format.FormatR8G8B8A8Unorm, BackFormat = Format.FormatB8G8R8A8Unorm, DepthFormat = Format.FormatD32Float;

    private D3D12 _d3d = null!;
    private DXGI _dxgi = null!;
    private IDXGIFactory4* _factory;
    private ID3D12Device* _dev;
    private ID3D12CommandQueue* _queue;
    private readonly nint[] _alloc = new nint[FramesInFlight];
    private ID3D12GraphicsCommandList* _list;
    private ID3D12Fence* _fence;
    private ulong _fenceValue;
    private readonly ulong[] _frameFence = new ulong[FramesInFlight];
    private readonly AutoResetEvent _fenceEvent = new(false);
    private int _frame;

    private IWindow? _window;
    private IDXGISwapChain3* _swap;
    private readonly nint[] _back = new nint[2];
    private int _backW, _backH;
    private int _w, _h;

    // Descriptor heaps: RTV (0 = off-screen, 1-2 = back buffers), DSV, shader-visible SRV (0 = fill texture, 1 = off-screen).
    private ID3D12DescriptorHeap* _rtvHeap, _dsvHeap, _srvHeap;
    private uint _rtvInc, _srvInc;

    private ID3D12RootSignature* _rootSig;
    private ID3D12Resource* _color, _depth, _ring, _dummy;
    private byte* _ringPtr;
    private int _drawSlot;
    private ResourceStates _colorState = ResourceStates.RenderTarget;

    private nint _geoPso, _shadePso, _partPso, _fillPso, _blitPso, _rectPso, _simPso, _vramWritePso, _vramVerifyPso;

    private readonly List<string> _sceneNames = new();
    private ID3D12Resource* _sphereVb, _sphereIb, _particles, _fillTex;
    private uint _sphereIndexCount, _sphereVbBytes, _sphereIbBytes;
    private bool _particlesAsUav = true;

    private readonly List<(nint Buf, long Words)> _vram = new();
    private ID3D12Resource* _errBuf, _errReadback;
    private uint _vramPass;

    public string Api => Core.Gpu.GpuApis.D3D12;
    public string Renderer { get; private set; } = "";
    public string Vendor { get; private set; } = "";
    public string Version { get; private set; } = "Direct3D 12";

    public WindowOptions ConfigureWindow(WindowOptions o) => o with { API = GraphicsAPI.None };

    // ------------------------------------------------------------------ setup

    public void Init(IWindow? window, int width, int height, bool software)
    {
        _window = window; _w = width; _h = height;
        _d3d = D3D12.GetApi();
        _dxgi = DXGI.GetApi(null, false);
        IDXGIFactory4* factory;
        var gf = IDXGIFactory4.Guid;
        D3DCommon.Check(_dxgi.CreateDXGIFactory2(0, &gf, (void**)&factory), "DXGI factory");
        _factory = factory;

        IDXGIAdapter1* adapter = null;
        var ga = IDXGIAdapter1.Guid;
        if (software) D3DCommon.Check(_factory->EnumWarpAdapter(&ga, (void**)&adapter), "WARP adapter");
        else
        {
            for (uint i = 0; _factory->EnumAdapters1(i, &adapter) >= 0; i++)
            {
                AdapterDesc1 d; adapter->GetDesc1(&d);
                if ((d.Flags & (uint)AdapterFlag.Software) == 0) break;
                adapter->Release(); adapter = null;
            }
        }
        if (adapter == null) throw new InvalidOperationException("no DirectX 12 graphics adapter found");
        AdapterDesc1 desc; adapter->GetDesc1(&desc);
        Renderer = new string((char*)desc.Description).TrimEnd('\0');
        Vendor = desc.VendorId switch { 0x10DE => "NVIDIA", 0x1002 => "AMD", 0x8086 => "Intel", 0x1414 => "Microsoft", _ => $"Vendor 0x{desc.VendorId:X4}" };
        Version = $"Direct3D 12 (feature level 11.0+), {desc.DedicatedVideoMemory / (1024 * 1024)} MB dedicated video memory";

        ID3D12Device* dev;
        var gd = ID3D12Device.Guid;
        int hr = _d3d.CreateDevice((IUnknown*)adapter, D3DFeatureLevel.Level110, &gd, (void**)&dev);
        adapter->Release();
        D3DCommon.Check(hr, "Direct3D 12 device (needs a DirectX 12 graphics driver)");
        _dev = dev;

        var qd = new CommandQueueDesc { Type = CommandListType.Direct };
        ID3D12CommandQueue* q; var gq = ID3D12CommandQueue.Guid;
        D3DCommon.Check(_dev->CreateCommandQueue(&qd, &gq, (void**)&q), "command queue"); _queue = q;
        var gal = ID3D12CommandAllocator.Guid;
        for (int i = 0; i < FramesInFlight; i++)
        {
            ID3D12CommandAllocator* a;
            D3DCommon.Check(_dev->CreateCommandAllocator(CommandListType.Direct, &gal, (void**)&a), "command allocator");
            _alloc[i] = (nint)a;
        }
        ID3D12GraphicsCommandList* list; var gl = ID3D12GraphicsCommandList.Guid;
        D3DCommon.Check(_dev->CreateCommandList(0, CommandListType.Direct, (ID3D12CommandAllocator*)_alloc[0], null, &gl, (void**)&list), "command list");
        _list = list;
        _list->Close();
        ID3D12Fence* fence; var gfe = ID3D12Fence.Guid;
        D3DCommon.Check(_dev->CreateFence(0, FenceFlags.None, &gfe, (void**)&fence), "fence"); _fence = fence;

        _rtvHeap = Heap(DescriptorHeapType.Rtv, 3, false);
        _dsvHeap = Heap(DescriptorHeapType.Dsv, 1, false);
        _srvHeap = Heap(DescriptorHeapType.CbvSrvUav, 2, true);
        _rtvInc = _dev->GetDescriptorHandleIncrementSize(DescriptorHeapType.Rtv);
        _srvInc = _dev->GetDescriptorHandleIncrementSize(DescriptorHeapType.CbvSrvUav);

        CreateOffscreen();
        CreateRootSignature();
        _ring = Buffer((FramesInFlight * MaxDrawsPerFrame + 64) * SceneParams.Stride, HeapType.Upload, ResourceStates.GenericRead);
        void* p; D3DCommon.Check(_ring->Map(0, null, &p), "map ring"); _ringPtr = (byte*)p;
        _dummy = Buffer(256, HeapType.Default, ResourceStates.UnorderedAccess, uav: true);
        if (window != null) CreateSwapchain();
    }

    private ID3D12DescriptorHeap* Heap(DescriptorHeapType type, uint n, bool shaderVisible)
    {
        var d = new DescriptorHeapDesc { Type = type, NumDescriptors = n, Flags = shaderVisible ? DescriptorHeapFlags.ShaderVisible : DescriptorHeapFlags.None };
        ID3D12DescriptorHeap* h; var g = ID3D12DescriptorHeap.Guid;
        D3DCommon.Check(_dev->CreateDescriptorHeap(&d, &g, (void**)&h), "descriptor heap");
        return h;
    }

    private CpuDescriptorHandle Rtv(int i) => new() { Ptr = _rtvHeap->GetCPUDescriptorHandleForHeapStart().Ptr + (nuint)(i * _rtvInc) };
    private CpuDescriptorHandle SrvCpu(int i) => new() { Ptr = _srvHeap->GetCPUDescriptorHandleForHeapStart().Ptr + (nuint)(i * _srvInc) };
    private GpuDescriptorHandle SrvGpu(int i) => new() { Ptr = _srvHeap->GetGPUDescriptorHandleForHeapStart().Ptr + (ulong)(i * _srvInc) };

    private ID3D12Resource* Buffer(long bytes, HeapType heap, ResourceStates state, bool uav = false)
    {
        var hp = new HeapProperties { Type = heap };
        var rd = new ResourceDesc
        {
            Dimension = ResourceDimension.Buffer, Width = (ulong)bytes, Height = 1, DepthOrArraySize = 1, MipLevels = 1, Format = Format.FormatUnknown,
            SampleDesc = new SampleDesc(1, 0), Layout = TextureLayout.LayoutRowMajor, Flags = uav ? ResourceFlags.AllowUnorderedAccess : ResourceFlags.None,
        };
        ID3D12Resource* r; var g = ID3D12Resource.Guid;
        D3DCommon.Check(_dev->CreateCommittedResource(&hp, HeapFlags.None, &rd, state, null, &g, (void**)&r), "buffer");
        return r;
    }

    private void CreateOffscreen()
    {
        var hp = new HeapProperties { Type = HeapType.Default };
        var rd = new ResourceDesc
        {
            Dimension = ResourceDimension.Texture2D, Width = (ulong)_w, Height = (uint)_h, DepthOrArraySize = 1, MipLevels = 1, Format = ColorFormat,
            SampleDesc = new SampleDesc(1, 0), Layout = TextureLayout.LayoutUnknown, Flags = ResourceFlags.AllowRenderTarget,
        };
        var cv = new ClearValue { Format = ColorFormat };
        ID3D12Resource* r; var g = ID3D12Resource.Guid;
        D3DCommon.Check(_dev->CreateCommittedResource(&hp, HeapFlags.None, &rd, ResourceStates.RenderTarget, null, &g, (void**)&r), "off-screen image");
        _color = r;
        _dev->CreateRenderTargetView(_color, null, Rtv(0));
        _dev->CreateShaderResourceView(_color, null, SrvCpu(1));
        var dd = rd with { Format = DepthFormat, Flags = ResourceFlags.AllowDepthStencil };
        var dcv = new ClearValue { Format = DepthFormat };
        dcv.DepthStencil = new DepthStencilValue(1, 0);
        D3DCommon.Check(_dev->CreateCommittedResource(&hp, HeapFlags.None, &dd, ResourceStates.DepthWrite, &dcv, &g, (void**)&r), "depth buffer");
        _depth = r;
        _dev->CreateDepthStencilView(_depth, null, _dsvHeap->GetCPUDescriptorHandleForHeapStart());
    }

    private void CreateRootSignature()
    {
        // 0: b0 (root CBV), 1: t1 (root SRV), 2-4: u0-u2 (root UAVs), 5: table with t0. Static sampler s0.
        var range = new DescriptorRange { RangeType = DescriptorRangeType.Srv, NumDescriptors = 1, BaseShaderRegister = 0, OffsetInDescriptorsFromTableStart = 0 };
        var ps = stackalloc RootParameter[6];
        ps[0] = new RootParameter { ParameterType = RootParameterType.TypeCbv, ShaderVisibility = ShaderVisibility.All }; ps[0].Descriptor = new RootDescriptor { ShaderRegister = 0 };
        ps[1] = new RootParameter { ParameterType = RootParameterType.TypeSrv, ShaderVisibility = ShaderVisibility.All }; ps[1].Descriptor = new RootDescriptor { ShaderRegister = 1 };
        for (int i = 0; i < 3; i++) { ps[2 + i] = new RootParameter { ParameterType = RootParameterType.TypeUav, ShaderVisibility = ShaderVisibility.All }; ps[2 + i].Descriptor = new RootDescriptor { ShaderRegister = (uint)i }; }
        ps[5] = new RootParameter { ParameterType = RootParameterType.TypeDescriptorTable, ShaderVisibility = ShaderVisibility.All };
        ps[5].DescriptorTable = new RootDescriptorTable { NumDescriptorRanges = 1, PDescriptorRanges = &range };
        var sampler = new StaticSamplerDesc
        {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Wrap, AddressV = TextureAddressMode.Wrap, AddressW = TextureAddressMode.Wrap,
            MaxLOD = float.MaxValue, ComparisonFunc = ComparisonFunc.Never, ShaderRegister = 0, ShaderVisibility = ShaderVisibility.All,
        };
        var desc = new RootSignatureDesc { NumParameters = 6, PParameters = ps, NumStaticSamplers = 1, PStaticSamplers = &sampler, Flags = RootSignatureFlags.AllowInputAssemblerInputLayout };
        ID3D10Blob* blob = null, err = null;
        int hr = _d3d.SerializeRootSignature(&desc, D3DRootSignatureVersion.Version1, &blob, &err);
        if (hr < 0)
        {
            var msg = err != null ? System.Text.Encoding.UTF8.GetString((byte*)err->GetBufferPointer(), (int)err->GetBufferSize()) : $"0x{hr:X8}";
            throw new InvalidOperationException("root signature: " + msg);
        }
        ID3D12RootSignature* rs; var g = ID3D12RootSignature.Guid;
        D3DCommon.Check(_dev->CreateRootSignature(0, blob->GetBufferPointer(), blob->GetBufferSize(), &g, (void**)&rs), "root signature");
        _rootSig = rs;
        blob->Release();
        if (err != null) err->Release();
    }

    private void CreateSwapchain()
    {
        var fb = _window!.FramebufferSize;
        _backW = Math.Max(1, fb.X); _backH = Math.Max(1, fb.Y);
        var sd = new SwapChainDesc1
        {
            Width = (uint)_backW, Height = (uint)_backH, Format = BackFormat, SampleDesc = new SampleDesc(1, 0), BufferUsage = DXGI.UsageRenderTargetOutput,
            BufferCount = 2, SwapEffect = SwapEffect.FlipDiscard, Scaling = Scaling.Stretch, AlphaMode = AlphaMode.Ignore,
        };
        IDXGISwapChain1* sc1;
        D3DCommon.Check(_factory->CreateSwapChainForHwnd((IUnknown*)_queue, _window.Native!.Win32!.Value.Hwnd, &sd, null, null, &sc1), "swap chain");
        IDXGISwapChain3* sc3; var g = IDXGISwapChain3.Guid;
        D3DCommon.Check(sc1->QueryInterface(&g, (void**)&sc3), "swap chain 3");
        sc1->Release();
        _swap = sc3;
        CreateBackViews();
    }

    private void CreateBackViews()
    {
        var g = ID3D12Resource.Guid;
        for (uint i = 0; i < 2; i++)
        {
            ID3D12Resource* b;
            D3DCommon.Check(_swap->GetBuffer(i, &g, (void**)&b), "back buffer");
            _back[i] = (nint)b;
            _dev->CreateRenderTargetView(b, null, Rtv(1 + (int)i));
        }
    }

    // ------------------------------------------------------------------ pipelines

    private static ShaderBytecode Code(byte[] b, GCHandle h) => new() { PShaderBytecode = (void*)h.AddrOfPinnedObject(), BytecodeLength = (nuint)b.Length };

    private nint Graphics(string vs, string ps, Format rtv, bool depthTest, int blend, bool geoInput)
    {
        var vsCode = D3DCommon.Compile(vs, "vs_5_0"); var psCode = D3DCommon.Compile(ps, "ps_5_0");
        var hv = GCHandle.Alloc(vsCode, GCHandleType.Pinned); var hpx = GCHandle.Alloc(psCode, GCHandleType.Pinned);
        var sem = (byte*)SilkMarshal.StringToPtr("POSITION");
        try
        {
            var el = new InputElementDesc { SemanticName = sem, Format = Format.FormatR32G32B32Float, InputSlotClass = InputClassification.PerVertexData };
            var d = new GraphicsPipelineStateDesc
            {
                PRootSignature = _rootSig, VS = Code(vsCode, hv), PS = Code(psCode, hpx), SampleMask = uint.MaxValue,
                PrimitiveTopologyType = PrimitiveTopologyType.Triangle, NumRenderTargets = 1, SampleDesc = new SampleDesc(1, 0),
                DSVFormat = rtv == ColorFormat ? DepthFormat : Format.FormatUnknown,
                RasterizerState = new RasterizerDesc { FillMode = FillMode.Solid, CullMode = CullMode.None, DepthClipEnable = true },
                DepthStencilState = new DepthStencilDesc { DepthEnable = depthTest, DepthWriteMask = depthTest ? DepthWriteMask.All : DepthWriteMask.Zero, DepthFunc = depthTest ? ComparisonFunc.Less : ComparisonFunc.Always },
            };
            d.RTVFormats[0] = rtv;
            if (geoInput) d.InputLayout = new InputLayoutDesc { PInputElementDescs = &el, NumElements = 1 };
            var (src, dst) = blend switch { 1 => (Blend.One, Blend.One), 2 => (Blend.SrcAlpha, Blend.InvSrcAlpha), _ => (Blend.One, Blend.Zero) };
            d.BlendState.RenderTarget[0] = new RenderTargetBlendDesc
            {
                BlendEnable = blend != 0, SrcBlend = src, DestBlend = dst, BlendOp = BlendOp.Add, SrcBlendAlpha = src, DestBlendAlpha = dst, BlendOpAlpha = BlendOp.Add,
                LogicOp = LogicOp.Noop, RenderTargetWriteMask = (byte)ColorWriteEnable.All,
            };
            ID3D12PipelineState* pso; var g = ID3D12PipelineState.Guid;
            D3DCommon.Check(_dev->CreateGraphicsPipelineState(&d, &g, (void**)&pso), $"pipeline {vs}/{ps}");
            return (nint)pso;
        }
        finally { hv.Free(); hpx.Free(); SilkMarshal.Free((nint)sem); }
    }

    private nint Compute(string cs)
    {
        var code = D3DCommon.Compile(cs, "cs_5_0");
        var h = GCHandle.Alloc(code, GCHandleType.Pinned);
        try
        {
            var d = new ComputePipelineStateDesc { PRootSignature = _rootSig, CS = Code(code, h) };
            ID3D12PipelineState* pso; var g = ID3D12PipelineState.Guid;
            D3DCommon.Check(_dev->CreateComputePipelineState(&d, &g, (void**)&pso), "compute pipeline " + cs);
            return (nint)pso;
        }
        finally { h.Free(); }
    }

    public void CreateScenes(IReadOnlyList<string> names)
    {
        _sceneNames.AddRange(names);
        _geoPso = Graphics("GeoVS", "GeoPS", ColorFormat, true, 0, true);
        _shadePso = Graphics("FullVS", "ShadePS", ColorFormat, false, 0, false);
        _partPso = Graphics("PartVS", "PartPS", ColorFormat, false, 1, false);
        _fillPso = Graphics("FullVS", "FillPS", ColorFormat, false, 2, false);
        _blitPso = Graphics("FullVS", "BlitPS", BackFormat, false, 0, false);
        _rectPso = Graphics("RectVS", "RectPS", BackFormat, false, 0, false);
        _simPso = Compute("SimCS"); _vramWritePso = Compute("VramWriteCS"); _vramVerifyPso = Compute("VramVerifyCS");

        var (verts, idx) = SceneMath.Sphere();
        _sphereVbBytes = (uint)(verts.Length * 4); _sphereIbBytes = (uint)(idx.Length * 4);
        _sphereVb = Upload(MemoryMarshal.AsBytes(verts.AsSpan()), ResourceStates.VertexAndConstantBuffer, false);
        _sphereIb = Upload(MemoryMarshal.AsBytes(idx.AsSpan()), ResourceStates.IndexBuffer, false);
        _sphereIndexCount = (uint)idx.Length;
        var pdata = SceneMath.ParticleData();
        _particles = Upload(MemoryMarshal.AsBytes(pdata.AsSpan()), ResourceStates.UnorderedAccess, true);
        _particlesAsUav = true;
        UploadFillTexture();
    }

    private ID3D12Resource* Upload(ReadOnlySpan<byte> data, ResourceStates final, bool uav)
    {
        var staging = Buffer(data.Length, HeapType.Upload, ResourceStates.GenericRead);
        void* p; D3DCommon.Check(staging->Map(0, null, &p), "map staging");
        data.CopyTo(new Span<byte>(p, data.Length));
        staging->Unmap(0, null);
        var dst = Buffer(data.Length, HeapType.Default, ResourceStates.CopyDest, uav);
        ulong len = (ulong)data.Length;
        OneShot(() =>
        {
            _list->CopyBufferRegion(dst, 0, staging, 0, len);
            Barrier(dst, ResourceStates.CopyDest, final);
        });
        staging->Release();
        return dst;
    }

    private void UploadFillTexture()
    {
        var mips = SceneMath.Mips(SceneMath.FillTexture(), SceneMath.FillTextureSize);
        var hp = new HeapProperties { Type = HeapType.Default };
        var rd = new ResourceDesc
        {
            Dimension = ResourceDimension.Texture2D, Width = SceneMath.FillTextureSize, Height = SceneMath.FillTextureSize, DepthOrArraySize = 1,
            MipLevels = (ushort)mips.Count, Format = ColorFormat, SampleDesc = new SampleDesc(1, 0), Layout = TextureLayout.LayoutUnknown,
        };
        ID3D12Resource* tex; var g = ID3D12Resource.Guid;
        D3DCommon.Check(_dev->CreateCommittedResource(&hp, HeapFlags.None, &rd, ResourceStates.CopyDest, null, &g, (void**)&tex), "fill texture");
        _fillTex = tex;
        var layouts = new PlacedSubresourceFootprint[mips.Count];
        var rows = new uint[mips.Count]; var rowBytes = new ulong[mips.Count]; ulong total;
        fixed (PlacedSubresourceFootprint* l = layouts) fixed (uint* r = rows) fixed (ulong* rb = rowBytes)
            _dev->GetCopyableFootprints(&rd, 0, (uint)mips.Count, 0, l, r, rb, &total);
        var staging = Buffer((long)total, HeapType.Upload, ResourceStates.GenericRead);
        void* p; D3DCommon.Check(staging->Map(0, null, &p), "map texture staging");
        for (int i = 0; i < mips.Count; i++)
        {
            var src = MemoryMarshal.AsBytes(mips[i].AsSpan());
            int rowLen = (int)rowBytes[i];
            for (int y = 0; y < rows[i]; y++)
                src.Slice(y * rowLen, rowLen).CopyTo(new Span<byte>((byte*)p + (long)layouts[i].Offset + (long)y * layouts[i].Footprint.RowPitch, rowLen));
        }
        staging->Unmap(0, null);
        OneShot(() =>
        {
            for (int i = 0; i < mips.Count; i++)
            {
                var dst = new TextureCopyLocation { PResource = _fillTex, Type = TextureCopyType.SubresourceIndex }; dst.SubresourceIndex = (uint)i;
                var srcLoc = new TextureCopyLocation { PResource = staging, Type = TextureCopyType.PlacedFootprint }; srcLoc.PlacedFootprint = layouts[i];
                _list->CopyTextureRegion(&dst, 0, 0, 0, &srcLoc, null);
            }
            Barrier(_fillTex, ResourceStates.CopyDest, ResourceStates.PixelShaderResource);
        });
        staging->Release();
        _dev->CreateShaderResourceView(_fillTex, null, SrvCpu(0));
    }

    // ------------------------------------------------------------------ frames

    private void Barrier(ID3D12Resource* r, ResourceStates from, ResourceStates to)
    {
        if (from == to) return;
        var b = new ResourceBarrier { Type = ResourceBarrierType.Transition };
        b.Transition = new ResourceTransitionBarrier { PResource = r, Subresource = 0xFFFFFFFF, StateBefore = from, StateAfter = to };
        _list->ResourceBarrier(1, &b);
    }

    private void UavBarrier(ID3D12Resource* r)
    {
        var b = new ResourceBarrier { Type = ResourceBarrierType.Uav };
        b.UAV = new ResourceUavBarrier { PResource = r };
        _list->ResourceBarrier(1, &b);
    }

    private void SetColorState(ResourceStates s) { Barrier(_color, _colorState, s); _colorState = s; }

    private void WaitFor(ulong value)
    {
        if (_fence->GetCompletedValue() >= value) return;
        D3DCommon.Check(_fence->SetEventOnCompletion(value, (void*)_fenceEvent.SafeWaitHandle.DangerousGetHandle()), "fence wait");
        _fenceEvent.WaitOne();
    }

    private ulong Submit()
    {
        _list->Close();
        var l = (ID3D12CommandList*)_list;
        _queue->ExecuteCommandLists(1, &l);
        _queue->Signal(_fence, ++_fenceValue);
        return _fenceValue;
    }

    private void OneShot(Action record)
    {
        WaitIdle();
        var a = (ID3D12CommandAllocator*)_alloc[_frame];
        a->Reset();
        _list->Reset(a, null);
        record();
        WaitFor(Submit());
    }

    public void BeginFrame()
    {
        _frame = (_frame + 1) % FramesInFlight;
        WaitFor(_frameFence[_frame]);
        if (_swap != null)
        {
            var fb = _window!.FramebufferSize;
            if (fb.X > 0 && fb.Y > 0 && (fb.X != _backW || fb.Y != _backH))
            {
                WaitIdle();
                foreach (var b in _back) ((ID3D12Resource*)b)->Release();
                D3DCommon.Check(_swap->ResizeBuffers(0, (uint)fb.X, (uint)fb.Y, Format.FormatUnknown, 0), "resize");
                _backW = fb.X; _backH = fb.Y;
                CreateBackViews();
            }
        }
        var a = (ID3D12CommandAllocator*)_alloc[_frame];
        a->Reset();
        _list->Reset(a, null);
        var heap = _srvHeap;
        _list->SetDescriptorHeaps(1, &heap);
        _list->SetGraphicsRootSignature(_rootSig);
        _list->SetComputeRootSignature(_rootSig);
        // Every root slot gets a valid address, even the ones a shader doesn't use.
        ulong dummy = _dummy->GetGPUVirtualAddress();
        for (uint i = 1; i <= 4; i++)
        {
            if (i == 1) { _list->SetGraphicsRootShaderResourceView(1, dummy); _list->SetComputeRootShaderResourceView(1, dummy); }
            else { _list->SetGraphicsRootUnorderedAccessView(i, dummy); _list->SetComputeRootUnorderedAccessView(i, dummy); }
        }
        _list->SetGraphicsRootDescriptorTable(5, SrvGpu(0));
        _list->SetComputeRootDescriptorTable(5, SrvGpu(0));
        _drawSlot = 0;
    }

    private ulong Param(in SceneParams p)
    {
        int slot = _frame * MaxDrawsPerFrame + Math.Min(_drawSlot++, MaxDrawsPerFrame - 1);
        Unsafe.Write(_ringPtr + slot * SceneParams.Stride, p);
        return _ring->GetGPUVirtualAddress() + (ulong)(slot * SceneParams.Stride);
    }

    private void SetTarget(CpuDescriptorHandle rtv, bool depth, int w, int h)
    {
        var dsv = _dsvHeap->GetCPUDescriptorHandleForHeapStart();
        _list->OMSetRenderTargets(1, &rtv, false, depth ? &dsv : null);
        var vp = new Viewport(0, 0, w, h, 0, 1);
        _list->RSSetViewports(1, &vp);
        var sc = new Box2D<int>(0, 0, w, h);
        _list->RSSetScissorRects(1, &sc);
        _list->IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
    }

    public void RenderScene(int index, double time)
    {
        string name = _sceneNames[index];
        if (name == Core.Gpu.GpuScenes.Compute)
        {
            if (!_particlesAsUav) { Barrier(_particles, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess); _particlesAsUav = true; }
            _list->SetPipelineState((ID3D12PipelineState*)_simPso);
            _list->SetComputeRootConstantBufferView(0, Param(SceneMath.Compute(time, _w, _h)));
            _list->SetComputeRootUnorderedAccessView(2, _particles->GetGPUVirtualAddress());
            for (int s = 0; s < SceneMath.ComputeSteps; s++) { _list->Dispatch(SceneMath.Particles / 256, 1, 1); UavBarrier(_particles); }
            Barrier(_particles, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
            _particlesAsUav = false;
        }

        SetColorState(ResourceStates.RenderTarget);
        SetTarget(Rtv(0), true, _w, _h);
        var clear = name switch
        {
            Core.Gpu.GpuScenes.Geometry => stackalloc float[] { 0.02f, 0.03f, 0.05f, 1 },
            Core.Gpu.GpuScenes.Compute => stackalloc float[] { 0, 0, 0.01f, 1 },
            _ => stackalloc float[] { 0, 0, 0, 1 },
        };
        fixed (float* c = clear) _list->ClearRenderTargetView(Rtv(0), c, 0, null);
        _list->ClearDepthStencilView(_dsvHeap->GetCPUDescriptorHandleForHeapStart(), ClearFlags.Depth, 1, 0, 0, null);

        switch (name)
        {
            case Core.Gpu.GpuScenes.Geometry:
            {
                _list->SetPipelineState((ID3D12PipelineState*)_geoPso);
                var vbv = new VertexBufferView { BufferLocation = _sphereVb->GetGPUVirtualAddress(), SizeInBytes = _sphereVbBytes, StrideInBytes = 12 };
                var ibv = new IndexBufferView { BufferLocation = _sphereIb->GetGPUVirtualAddress(), SizeInBytes = _sphereIbBytes, Format = Format.FormatR32Uint };
                _list->IASetVertexBuffers(0, 1, &vbv);
                _list->IASetIndexBuffer(&ibv);
                for (int b = 0; b < SceneMath.GeometryBatches; b++)
                {
                    _list->SetGraphicsRootConstantBufferView(0, Param(SceneMath.Geometry(time, _w, _h, b)));
                    _list->DrawIndexedInstanced(_sphereIndexCount, SceneMath.GeometryInstances / SceneMath.GeometryBatches, 0, 0, 0);
                }
                break;
            }
            case Core.Gpu.GpuScenes.Shading:
            {
                _list->SetPipelineState((ID3D12PipelineState*)_shadePso);
                _list->SetGraphicsRootConstantBufferView(0, Param(SceneMath.Shading(time, _w, _h)));
                for (int i = 0; i < SceneMath.ShadingBands; i++)
                {
                    int y0 = _h * i / SceneMath.ShadingBands, y1 = _h * (i + 1) / SceneMath.ShadingBands;
                    var band = new Box2D<int>(0, y0, _w, y1);
                    _list->RSSetScissorRects(1, &band);
                    _list->DrawInstanced(3, 1, 0, 0);
                }
                break;
            }
            case Core.Gpu.GpuScenes.Compute:
            {
                _list->SetPipelineState((ID3D12PipelineState*)_partPso);
                _list->SetGraphicsRootConstantBufferView(0, Param(SceneMath.Compute(time, _w, _h)));
                _list->SetGraphicsRootShaderResourceView(1, _particles->GetGPUVirtualAddress());
                _list->DrawInstanced(SceneMath.Particles * 6, 1, 0, 0);
                break;
            }
            default:
            {
                _list->SetPipelineState((ID3D12PipelineState*)_fillPso);
                _list->SetGraphicsRootDescriptorTable(5, SrvGpu(0));
                for (int layer = 0; layer < SceneMath.FillLayers; layer++)
                {
                    _list->SetGraphicsRootConstantBufferView(0, Param(SceneMath.Fill(time, _w, _h, layer)));
                    _list->DrawInstanced(3, 1, 0, 0);
                }
                break;
            }
        }
    }

    public void EndFrame(bool showScene, double overall, int sceneIndex, int sceneCount)
    {
        if (_swap != null)
        {
            uint bi = _swap->GetCurrentBackBufferIndex();
            var back = (ID3D12Resource*)_back[bi];
            Barrier(back, ResourceStates.Present, ResourceStates.RenderTarget);
            SetTarget(Rtv(1 + (int)bi), false, _backW, _backH);
            var black = stackalloc float[] { 0, 0, 0, 1 };
            _list->ClearRenderTargetView(Rtv(1 + (int)bi), black, 0, null);
            if (showScene)
            {
                SetColorState(ResourceStates.PixelShaderResource);
                _list->SetPipelineState((ID3D12PipelineState*)_blitPso);
                _list->SetGraphicsRootConstantBufferView(0, Param(default));
                _list->SetGraphicsRootDescriptorTable(5, SrvGpu(1));
                _list->DrawInstanced(3, 1, 0, 0);
                _list->SetGraphicsRootDescriptorTable(5, SrvGpu(0));
            }
            _list->SetPipelineState((ID3D12PipelineState*)_rectPso);
            foreach (var (r, c) in SceneMath.Overlay(overall, sceneIndex, sceneCount, _backH))
            {
                _list->SetGraphicsRootConstantBufferView(0, Param(new SceneParams { Rect = r, Color = c }));
                _list->DrawInstanced(6, 1, 0, 0);
            }
            Barrier(back, ResourceStates.RenderTarget, ResourceStates.Present);
        }
        SetColorState(ResourceStates.RenderTarget);
        _frameFence[_frame] = Submit();
        if (_swap != null) _swap->Present(0, 0);
    }

    // ------------------------------------------------------------------ VRAM, PCIe, screenshots

    public long InitVram(long targetBytes)
    {
        _errBuf = Buffer(16, HeapType.Default, ResourceStates.UnorderedAccess, uav: true);
        _errReadback = Buffer(16, HeapType.Readback, ResourceStates.CopyDest);
        long chunk = 128L << 20, total = 0;
        while (total < targetBytes)
        {
            long bytes = Math.Min(chunk, targetBytes - total);
            try { _vram.Add(((nint)Buffer(bytes, HeapType.Default, ResourceStates.UnorderedAccess, uav: true), bytes / 4)); }
            catch { break; } // out of video memory: test what we got
            total += bytes;
        }
        return total;
    }

    public (long Errors, string? Detail) VramPass()
    {
        _vramPass++;
        uint seed = _vramPass * 2654435761u;
        var zeroBuf = Buffer(16, HeapType.Upload, ResourceStates.GenericRead);
        void* zp; zeroBuf->Map(0, null, &zp); new Span<byte>(zp, 16).Clear(); zeroBuf->Unmap(0, null);
        OneShot(() =>
        {
            var heap = _srvHeap;
            _list->SetDescriptorHeaps(1, &heap);
            _list->SetComputeRootSignature(_rootSig);
            ulong dummy = _dummy->GetGPUVirtualAddress();
            _list->SetComputeRootShaderResourceView(1, dummy);
            _list->SetComputeRootUnorderedAccessView(2, dummy);
            _list->SetComputeRootDescriptorTable(5, SrvGpu(0));
            Barrier(_errBuf, ResourceStates.UnorderedAccess, ResourceStates.CopyDest);
            _list->CopyBufferRegion(_errBuf, 0, zeroBuf, 0, 16);
            Barrier(_errBuf, ResourceStates.CopyDest, ResourceStates.UnorderedAccess);
            _list->SetComputeRootUnorderedAccessView(4, _errBuf->GetGPUVirtualAddress());
            int slot = 0;
            foreach (var (buf, words) in _vram)
            {
                uint groups = (uint)Math.Min(65535, (words + 255) / 256);
                int s = FramesInFlight * MaxDrawsPerFrame + Math.Min(slot++, 63);
                Unsafe.Write(_ringPtr + s * SceneParams.Stride, new SceneParams { Seed = seed, Count = (uint)words, Base = groups * 256 });
                _list->SetComputeRootConstantBufferView(0, _ring->GetGPUVirtualAddress() + (ulong)(s * SceneParams.Stride));
                _list->SetComputeRootUnorderedAccessView(3, ((ID3D12Resource*)buf)->GetGPUVirtualAddress());
                _list->SetPipelineState((ID3D12PipelineState*)_vramWritePso);
                _list->Dispatch(groups, 1, 1);
                UavBarrier((ID3D12Resource*)buf);
                _list->SetPipelineState((ID3D12PipelineState*)_vramVerifyPso);
                _list->Dispatch(groups, 1, 1);
                UavBarrier((ID3D12Resource*)buf);
            }
            Barrier(_errBuf, ResourceStates.UnorderedAccess, ResourceStates.CopySource);
            _list->CopyBufferRegion(_errReadback, 0, _errBuf, 0, 16);
            Barrier(_errBuf, ResourceStates.CopySource, ResourceStates.UnorderedAccess);
        });
        zeroBuf->Release();
        void* p; var range = new D3D12Range(0, 16);
        _errReadback->Map(0, &range, &p);
        var r = new ReadOnlySpan<uint>(p, 4).ToArray();
        var none = new D3D12Range(0, 0);
        _errReadback->Unmap(0, &none);
        return r[0] == 0 ? (0, null) : (r[0], $"first bad word #{r[1]}: expected {r[2]:X8} read {r[3]:X8}");
    }

    public (double UploadGBs, double DownloadGBs) TransferTest(long bytes)
    {
        var up = Buffer(bytes, HeapType.Upload, ResourceStates.GenericRead);
        var gpu = Buffer(bytes, HeapType.Default, ResourceStates.CopyDest);
        var rb = Buffer(bytes, HeapType.Readback, ResourceStates.CopyDest);
        try
        {
            void* p; up->Map(0, null, &p);
            new Random(3).NextBytes(new Span<byte>(p, (int)bytes));
            up->Unmap(0, null);
            double best = 0, bestDown = 0;
            for (int pass = 0; pass < 3; pass++)
            {
                var sw = Stopwatch.StartNew();
                OneShot(() => { _list->CopyBufferRegion(gpu, 0, up, 0, (ulong)bytes); Barrier(gpu, ResourceStates.CopyDest, ResourceStates.CopySource); });
                best = Math.Max(best, bytes / sw.Elapsed.TotalSeconds / 1e9);
                sw.Restart();
                OneShot(() => { _list->CopyBufferRegion(rb, 0, gpu, 0, (ulong)bytes); Barrier(gpu, ResourceStates.CopySource, ResourceStates.CopyDest); });
                bestDown = Math.Max(bestDown, bytes / sw.Elapsed.TotalSeconds / 1e9);
            }
            return (best, bestDown);
        }
        finally { up->Release(); gpu->Release(); rb->Release(); }
    }

    public byte[] ReadPixels()
    {
        var rd = _color->GetDesc();
        var fpArr = new PlacedSubresourceFootprint[1]; uint rows; ulong rowBytes, total;
        fixed (PlacedSubresourceFootprint* f = fpArr) _dev->GetCopyableFootprints(&rd, 0, 1, 0, f, &rows, &rowBytes, &total);
        var fp = fpArr[0];
        var rb = Buffer((long)total, HeapType.Readback, ResourceStates.CopyDest);
        try
        {
            OneShot(() =>
            {
                SetColorState(ResourceStates.CopySource);
                var dst = new TextureCopyLocation { PResource = rb, Type = TextureCopyType.PlacedFootprint }; dst.PlacedFootprint = fp;
                var src = new TextureCopyLocation { PResource = _color, Type = TextureCopyType.SubresourceIndex }; src.SubresourceIndex = 0;
                _list->CopyTextureRegion(&dst, 0, 0, 0, &src, null);
                SetColorState(ResourceStates.RenderTarget);
            });
            void* p; rb->Map(0, null, &p);
            var px = new byte[_w * _h * 4];
            for (int y = 0; y < _h; y++)
                new ReadOnlySpan<byte>((byte*)p + (long)y * fp.Footprint.RowPitch, _w * 4).CopyTo(px.AsSpan(y * _w * 4));
            rb->Unmap(0, null);
            for (int i = 0; i < px.Length; i += 4) (px[i], px[i + 2]) = (px[i + 2], px[i]); // RGBA → BGRA
            return px;
        }
        finally { rb->Release(); }
    }

    public void WaitIdle()
    {
        if (_queue == null) return;
        _queue->Signal(_fence, ++_fenceValue);
        WaitFor(_fenceValue);
    }

    public void Dispose()
    {
        if (_dev == null) return;
        WaitIdle();
        foreach (var (b, _) in _vram) ((ID3D12Resource*)b)->Release();
        foreach (var b in _back) if (b != 0) ((ID3D12Resource*)b)->Release();
        if (_swap != null) _swap->Release();
        _list->Release();
        foreach (var a in _alloc) ((ID3D12CommandAllocator*)a)->Release();
        _queue->Release();
        _fence->Release();
        _dev->Release();
        _factory->Release();
        _dev = null;
    }
}
