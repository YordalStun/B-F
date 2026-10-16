using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Windowing;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkImage = Silk.NET.Vulkan.Image;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace BenchForge.Gpu;

/// <summary>Vulkan 1.1 backend: the same scenes as OpenGL, from HLSL compiled to SPIR-V.</summary>
internal sealed unsafe class VulkanBackend : IGpuBackend
{
    private const int FramesInFlight = 2, MaxDrawsPerFrame = 64;
    private const Format ColorFormat = Format.R8G8B8A8Unorm, DepthFormat = Format.D32Sfloat;

    private Vk _vk = null!;
    private Instance _instance;
    private PhysicalDevice _phys;
    private Device _device;
    private Queue _queue;
    private uint _queueFamily;
    private KhrSurface? _khrSurface;
    private KhrSwapchain? _khrSwapchain;
    private SurfaceKHR _surface;
    private SwapchainKHR _swapchain;
    private VkImage[] _swapImages = Array.Empty<VkImage>();
    private ImageView[] _swapViews = Array.Empty<ImageView>();
    private Framebuffer[] _swapFramebuffers = Array.Empty<Framebuffer>();
    private Format _swapFormat;
    private Extent2D _swapExtent;
    private RenderPass _overlayPass;
    private IWindow? _window;
    private bool _swapchainDirty;

    private int _w, _h;
    private PhysicalDeviceMemoryProperties _memProps;
    private ulong _uboAlign = 256;

    // Off-screen target
    private VkImage _color, _depth;
    private DeviceMemory _colorMem, _depthMem;
    private ImageView _colorView, _depthView;
    private RenderPass _scenePass;
    private Framebuffer _sceneFb;

    // Frames
    private CommandPool _pool;
    private readonly CommandBuffer[] _cmd = new CommandBuffer[FramesInFlight];
    private readonly Fence[] _fences = new Fence[FramesInFlight];
    private readonly VkSemaphore[] _imageReady = new VkSemaphore[FramesInFlight], _renderDone = new VkSemaphore[FramesInFlight];
    private int _frame;
    private uint _imageIndex;
    private bool _haveImage;

    // Shared resources
    private DescriptorSetLayout _setLayout;
    private PipelineLayout _pipeLayout;
    private DescriptorPool _descPool;
    private DescriptorSet _sceneSet;
    private (VkBuffer Buf, DeviceMemory Mem, nint Ptr) _params;
    private int _drawSlot;
    private (VkBuffer Buf, DeviceMemory Mem) _dummy;
    private Sampler _sampler;

    // Scenes
    private readonly List<string> _sceneNames = new();
    private Pipeline _geoPipe, _shadePipe, _partPipe, _fillPipe, _simPipe, _vramWritePipe, _vramVerifyPipe;
    private (VkBuffer Buf, DeviceMemory Mem) _sphereVb, _sphereIb, _particles;
    private uint _sphereIndexCount;
    private VkImage _fillTex; private DeviceMemory _fillMem; private ImageView _fillView;

    // VRAM check
    private readonly List<(VkBuffer Buf, DeviceMemory Mem, long Words, DescriptorSet Set)> _vram = new();
    private (VkBuffer Buf, DeviceMemory Mem, nint Ptr) _errors;
    private uint _vramPass;

    public string Api => Core.Gpu.GpuApis.Vulkan;
    public string Renderer { get; private set; } = "";
    public string Vendor { get; private set; } = "";
    public string Version { get; private set; } = "";

    public WindowOptions ConfigureWindow(WindowOptions o) => o with { API = GraphicsAPI.DefaultVulkan };

    // ------------------------------------------------------------------ setup

    public void Init(IWindow? window, int width, int height, bool software)
    {
        _window = window; _w = width; _h = height;
        _vk = Vk.GetApi();
        CreateInstance(window);
        if (window != null)
        {
            if (!_vk.TryGetInstanceExtension(_instance, out KhrSurface khrSurface)) throw new InvalidOperationException("VK_KHR_surface missing");
            _khrSurface = khrSurface;
            _surface = window.VkSurface!.Create<AllocationCallbacks>(_instance.ToHandle(), null).ToSurface();
        }
        PickDevice(software);
        CreateDevice();
        _vk.GetPhysicalDeviceMemoryProperties(_phys, out _memProps);

        var poolInfo = new CommandPoolCreateInfo { SType = StructureType.CommandPoolCreateInfo, QueueFamilyIndex = _queueFamily, Flags = CommandPoolCreateFlags.ResetCommandBufferBit };
        Check(_vk.CreateCommandPool(_device, in poolInfo, null, out _pool), "command pool");
        var alloc = new CommandBufferAllocateInfo { SType = StructureType.CommandBufferAllocateInfo, CommandPool = _pool, Level = CommandBufferLevel.Primary, CommandBufferCount = FramesInFlight };
        fixed (CommandBuffer* p = _cmd) Check(_vk.AllocateCommandBuffers(_device, in alloc, p), "command buffers");
        for (int i = 0; i < FramesInFlight; i++)
        {
            var fi = new FenceCreateInfo { SType = StructureType.FenceCreateInfo, Flags = FenceCreateFlags.SignaledBit };
            Check(_vk.CreateFence(_device, in fi, null, out _fences[i]), "fence");
            var si = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
            Check(_vk.CreateSemaphore(_device, in si, null, out _imageReady[i]), "semaphore");
            Check(_vk.CreateSemaphore(_device, in si, null, out _renderDone[i]), "semaphore");
        }

        CreateOffscreen();
        CreateDescriptors();
        if (window != null) CreateSwapchain();
    }

    private void CreateInstance(IWindow? window)
    {
        var appName = (byte*)SilkMarshal.StringToPtr("BenchForge");
        var app = new ApplicationInfo
        {
            SType = StructureType.ApplicationInfo, PApplicationName = appName, ApplicationVersion = new Version32(1, 0, 0),
            PEngineName = appName, EngineVersion = new Version32(1, 0, 0), ApiVersion = Vk.Version11,
        };
        var exts = new List<string>();
        if (window != null)
        {
            var req = window.VkSurface!.GetRequiredExtensions(out uint n);
            for (int i = 0; i < n; i++) exts.Add(SilkMarshal.PtrToString((nint)req[i])!);
        }
        var layers = new List<string>();
        if (Environment.GetEnvironmentVariable("BF_VK_VALIDATE") == "1") layers.Add("VK_LAYER_KHRONOS_validation");
        var extPtr = (byte**)SilkMarshal.StringArrayToPtr(exts.ToArray());
        var layerPtr = (byte**)SilkMarshal.StringArrayToPtr(layers.ToArray());
        var ci = new InstanceCreateInfo
        {
            SType = StructureType.InstanceCreateInfo, PApplicationInfo = &app,
            EnabledExtensionCount = (uint)exts.Count, PpEnabledExtensionNames = extPtr,
            EnabledLayerCount = (uint)layers.Count, PpEnabledLayerNames = layerPtr,
        };
        try { Check(_vk.CreateInstance(in ci, null, out _instance), "instance (is a Vulkan driver installed?)"); }
        finally { SilkMarshal.Free((nint)extPtr); SilkMarshal.Free((nint)layerPtr); SilkMarshal.Free((nint)appName); }
    }

    private void PickDevice(bool software)
    {
        uint count = 0;
        _vk.EnumeratePhysicalDevices(_instance, ref count, null);
        if (count == 0) throw new InvalidOperationException("no Vulkan device found");
        var devs = new PhysicalDevice[count];
        fixed (PhysicalDevice* p = devs) _vk.EnumeratePhysicalDevices(_instance, ref count, p);
        var candidates = new List<(PhysicalDevice Dev, uint Family, PhysicalDeviceProperties Props, int Rank)>();
        foreach (var d in devs)
        {
            _vk.GetPhysicalDeviceProperties(d, out var props);
            uint qn = 0;
            _vk.GetPhysicalDeviceQueueFamilyProperties(d, ref qn, null);
            var qs = new QueueFamilyProperties[qn];
            fixed (QueueFamilyProperties* q = qs) _vk.GetPhysicalDeviceQueueFamilyProperties(d, ref qn, q);
            for (uint i = 0; i < qn; i++)
            {
                if (!qs[i].QueueFlags.HasFlag(QueueFlags.GraphicsBit) || !qs[i].QueueFlags.HasFlag(QueueFlags.ComputeBit)) continue;
                if (_khrSurface != null)
                {
                    _khrSurface.GetPhysicalDeviceSurfaceSupport(d, i, _surface, out var present);
                    if (!present) continue;
                }
                bool cpu = props.DeviceType == PhysicalDeviceType.Cpu;
                int rank = software ? (cpu ? 0 : 1) : props.DeviceType switch { PhysicalDeviceType.DiscreteGpu => 0, PhysicalDeviceType.IntegratedGpu => 1, PhysicalDeviceType.VirtualGpu => 2, PhysicalDeviceType.Cpu => 4, _ => 3 };
                candidates.Add((d, i, props, rank));
                break;
            }
        }
        if (candidates.Count == 0) throw new InvalidOperationException("no Vulkan device can draw to this window");
        var best = candidates.OrderBy(c => c.Rank).First();
        _phys = best.Dev; _queueFamily = best.Family;
        var pr = best.Props;
        Renderer = SilkMarshal.PtrToString((nint)pr.DeviceName) ?? "Vulkan device";
        Vendor = pr.VendorID switch { 0x10DE => "NVIDIA", 0x1002 => "AMD", 0x8086 => "Intel", 0x10005 => "Mesa", _ => $"Vendor 0x{pr.VendorID:X4}" };
        Version = $"Vulkan {pr.ApiVersion >> 22}.{(pr.ApiVersion >> 12) & 0x3FF}.{pr.ApiVersion & 0xFFF} (driver 0x{pr.DriverVersion:X})";
        _uboAlign = Math.Max(256, pr.Limits.MinUniformBufferOffsetAlignment);
    }

    private void CreateDevice()
    {
        float prio = 1f;
        var qci = new DeviceQueueCreateInfo { SType = StructureType.DeviceQueueCreateInfo, QueueFamilyIndex = _queueFamily, QueueCount = 1, PQueuePriorities = &prio };
        var exts = _khrSurface != null ? new[] { KhrSwapchain.ExtensionName } : Array.Empty<string>();
        var extPtr = (byte**)SilkMarshal.StringArrayToPtr(exts);
        var features = new PhysicalDeviceFeatures();
        var ci = new DeviceCreateInfo
        {
            SType = StructureType.DeviceCreateInfo, QueueCreateInfoCount = 1, PQueueCreateInfos = &qci,
            EnabledExtensionCount = (uint)exts.Length, PpEnabledExtensionNames = extPtr, PEnabledFeatures = &features,
        };
        try { Check(_vk.CreateDevice(_phys, in ci, null, out _device), "device"); }
        finally { SilkMarshal.Free((nint)extPtr); }
        _vk.GetDeviceQueue(_device, _queueFamily, 0, out _queue);
        if (_khrSurface != null)
        {
            if (!_vk.TryGetDeviceExtension(_instance, _device, out KhrSwapchain sc)) throw new InvalidOperationException("VK_KHR_swapchain missing");
            _khrSwapchain = sc;
        }
    }

    private void CreateOffscreen()
    {
        (_color, _colorMem) = CreateImage((uint)_w, (uint)_h, 1, ColorFormat, ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.SampledBit);
        _colorView = CreateView(_color, ColorFormat, ImageAspectFlags.ColorBit, 1);
        (_depth, _depthMem) = CreateImage((uint)_w, (uint)_h, 1, DepthFormat, ImageUsageFlags.DepthStencilAttachmentBit);
        _depthView = CreateView(_depth, DepthFormat, ImageAspectFlags.DepthBit, 1);

        var attachments = stackalloc AttachmentDescription[2];
        attachments[0] = new AttachmentDescription
        {
            Format = ColorFormat, Samples = SampleCountFlags.Count1Bit, LoadOp = AttachmentLoadOp.Clear, StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare, StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined, FinalLayout = ImageLayout.TransferSrcOptimal,
        };
        attachments[1] = new AttachmentDescription
        {
            Format = DepthFormat, Samples = SampleCountFlags.Count1Bit, LoadOp = AttachmentLoadOp.Clear, StoreOp = AttachmentStoreOp.DontCare,
            StencilLoadOp = AttachmentLoadOp.DontCare, StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined, FinalLayout = ImageLayout.DepthStencilAttachmentOptimal,
        };
        var colorRef = new AttachmentReference(0, ImageLayout.ColorAttachmentOptimal);
        var depthRef = new AttachmentReference(1, ImageLayout.DepthStencilAttachmentOptimal);
        var subpass = new SubpassDescription { PipelineBindPoint = PipelineBindPoint.Graphics, ColorAttachmentCount = 1, PColorAttachments = &colorRef, PDepthStencilAttachment = &depthRef };
        var dep = new SubpassDependency
        {
            SrcSubpass = Vk.SubpassExternal, DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.TransferBit | PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.ComputeShaderBit,
            DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.VertexShaderBit,
            SrcAccessMask = AccessFlags.TransferReadBit | AccessFlags.ShaderWriteBit,
            DstAccessMask = AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit | AccessFlags.ShaderReadBit,
        };
        var rp = new RenderPassCreateInfo { SType = StructureType.RenderPassCreateInfo, AttachmentCount = 2, PAttachments = attachments, SubpassCount = 1, PSubpasses = &subpass, DependencyCount = 1, PDependencies = &dep };
        Check(_vk.CreateRenderPass(_device, in rp, null, out _scenePass), "render pass");
        var views = stackalloc ImageView[] { _colorView, _depthView };
        var fbi = new FramebufferCreateInfo { SType = StructureType.FramebufferCreateInfo, RenderPass = _scenePass, AttachmentCount = 2, PAttachments = views, Width = (uint)_w, Height = (uint)_h, Layers = 1 };
        Check(_vk.CreateFramebuffer(_device, in fbi, null, out _sceneFb), "framebuffer");
    }

    private void CreateDescriptors()
    {
        var b = stackalloc DescriptorSetLayoutBinding[7];
        b[0] = new DescriptorSetLayoutBinding(0, DescriptorType.UniformBufferDynamic, 1, ShaderStageFlags.All);
        b[1] = new DescriptorSetLayoutBinding(1, DescriptorType.SampledImage, 1, ShaderStageFlags.All);
        b[2] = new DescriptorSetLayoutBinding(2, DescriptorType.Sampler, 1, ShaderStageFlags.All);
        for (uint i = 3; i <= 6; i++) b[i] = new DescriptorSetLayoutBinding(i, DescriptorType.StorageBuffer, 1, ShaderStageFlags.All);
        var li = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 7, PBindings = b };
        Check(_vk.CreateDescriptorSetLayout(_device, in li, null, out _setLayout), "set layout");
        var setLayout = _setLayout;
        var pli = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 1, PSetLayouts = &setLayout };
        Check(_vk.CreatePipelineLayout(_device, in pli, null, out _pipeLayout), "pipeline layout");

        var sizes = stackalloc DescriptorPoolSize[4];
        sizes[0] = new DescriptorPoolSize(DescriptorType.UniformBufferDynamic, 128);
        sizes[1] = new DescriptorPoolSize(DescriptorType.SampledImage, 128);
        sizes[2] = new DescriptorPoolSize(DescriptorType.Sampler, 128);
        sizes[3] = new DescriptorPoolSize(DescriptorType.StorageBuffer, 512);
        var pi = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, MaxSets = 128, PoolSizeCount = 4, PPoolSizes = sizes };
        Check(_vk.CreateDescriptorPool(_device, in pi, null, out _descPool), "descriptor pool");

        long paramBytes = FramesInFlight * MaxDrawsPerFrame * (long)_uboAlign + 64 * (long)_uboAlign;
        _params = CreateMappedBuffer(paramBytes, BufferUsageFlags.UniformBufferBit);
        _dummy = CreateBuffer(256, BufferUsageFlags.StorageBufferBit, MemoryPropertyFlags.DeviceLocalBit);
        _errors = CreateMappedBuffer(16, BufferUsageFlags.StorageBufferBit);

        var si = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo, MagFilter = Filter.Linear, MinFilter = Filter.Linear, MipmapMode = SamplerMipmapMode.Linear,
            AddressModeU = SamplerAddressMode.Repeat, AddressModeV = SamplerAddressMode.Repeat, AddressModeW = SamplerAddressMode.Repeat, MaxLod = 16,
        };
        Check(_vk.CreateSampler(_device, in si, null, out _sampler), "sampler");
    }

    private void CreateSwapchain()
    {
        _khrSurface!.GetPhysicalDeviceSurfaceCapabilities(_phys, _surface, out var caps);
        uint fn = 0;
        _khrSurface.GetPhysicalDeviceSurfaceFormats(_phys, _surface, ref fn, null);
        var formats = new SurfaceFormatKHR[fn];
        fixed (SurfaceFormatKHR* f = formats) _khrSurface.GetPhysicalDeviceSurfaceFormats(_phys, _surface, ref fn, f);
        var fmt = formats.FirstOrDefault(f => f.Format == Format.B8G8R8A8Unorm, formats.FirstOrDefault(f => f.Format == Format.R8G8B8A8Unorm, formats[0]));
        _swapFormat = fmt.Format;
        uint pn = 0;
        _khrSurface.GetPhysicalDeviceSurfacePresentModes(_phys, _surface, ref pn, null);
        var modes = new PresentModeKHR[pn];
        fixed (PresentModeKHR* m = modes) _khrSurface.GetPhysicalDeviceSurfacePresentModes(_phys, _surface, ref pn, m);
        var mode = modes.Contains(PresentModeKHR.ImmediateKhr) ? PresentModeKHR.ImmediateKhr : modes.Contains(PresentModeKHR.MailboxKhr) ? PresentModeKHR.MailboxKhr : PresentModeKHR.FifoKhr;
        var fb = _window!.FramebufferSize;
        _swapExtent = caps.CurrentExtent.Width != uint.MaxValue ? caps.CurrentExtent
            : new Extent2D((uint)Math.Clamp(fb.X, (int)caps.MinImageExtent.Width, (int)caps.MaxImageExtent.Width), (uint)Math.Clamp(fb.Y, (int)caps.MinImageExtent.Height, (int)caps.MaxImageExtent.Height));
        if (_swapExtent.Width == 0 || _swapExtent.Height == 0) { _swapchainDirty = true; return; }
        uint imageCount = Math.Max(caps.MinImageCount, 2);
        if (caps.MaxImageCount > 0) imageCount = Math.Min(imageCount + 1, caps.MaxImageCount);
        var old = _swapchain;
        var ci = new SwapchainCreateInfoKHR
        {
            SType = StructureType.SwapchainCreateInfoKhr, Surface = _surface, MinImageCount = imageCount, ImageFormat = fmt.Format, ImageColorSpace = fmt.ColorSpace,
            ImageExtent = _swapExtent, ImageArrayLayers = 1, ImageUsage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferDstBit,
            ImageSharingMode = SharingMode.Exclusive, PreTransform = caps.CurrentTransform, CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
            PresentMode = mode, Clipped = true, OldSwapchain = old,
        };
        Check(_khrSwapchain!.CreateSwapchain(_device, in ci, null, out _swapchain), "swap chain");
        if (old.Handle != 0) DestroySwapchainResources(old);

        uint n = 0;
        _khrSwapchain.GetSwapchainImages(_device, _swapchain, ref n, null);
        _swapImages = new VkImage[n];
        fixed (VkImage* p = _swapImages) _khrSwapchain.GetSwapchainImages(_device, _swapchain, ref n, p);
        _swapViews = _swapImages.Select(i => CreateView(i, _swapFormat, ImageAspectFlags.ColorBit, 1)).ToArray();

        if (_overlayPass.Handle == 0)
        {
            var att = new AttachmentDescription
            {
                Format = _swapFormat, Samples = SampleCountFlags.Count1Bit, LoadOp = AttachmentLoadOp.Load, StoreOp = AttachmentStoreOp.Store,
                StencilLoadOp = AttachmentLoadOp.DontCare, StencilStoreOp = AttachmentStoreOp.DontCare,
                InitialLayout = ImageLayout.ColorAttachmentOptimal, FinalLayout = ImageLayout.PresentSrcKhr,
            };
            var r = new AttachmentReference(0, ImageLayout.ColorAttachmentOptimal);
            var sp = new SubpassDescription { PipelineBindPoint = PipelineBindPoint.Graphics, ColorAttachmentCount = 1, PColorAttachments = &r };
            var rpi = new RenderPassCreateInfo { SType = StructureType.RenderPassCreateInfo, AttachmentCount = 1, PAttachments = &att, SubpassCount = 1, PSubpasses = &sp };
            Check(_vk.CreateRenderPass(_device, in rpi, null, out _overlayPass), "overlay pass");
        }
        _swapFramebuffers = _swapViews.Select(v =>
        {
            var view = v;
            var fbi = new FramebufferCreateInfo { SType = StructureType.FramebufferCreateInfo, RenderPass = _overlayPass, AttachmentCount = 1, PAttachments = &view, Width = _swapExtent.Width, Height = _swapExtent.Height, Layers = 1 };
            Check(_vk.CreateFramebuffer(_device, in fbi, null, out var f), "swap framebuffer");
            return f;
        }).ToArray();
        _swapchainDirty = false;
    }

    private void DestroySwapchainResources(SwapchainKHR chain)
    {
        _vk.DeviceWaitIdle(_device);
        foreach (var f in _swapFramebuffers) _vk.DestroyFramebuffer(_device, f, null);
        foreach (var v in _swapViews) _vk.DestroyImageView(_device, v, null);
        _swapFramebuffers = Array.Empty<Framebuffer>(); _swapViews = Array.Empty<ImageView>();
        _khrSwapchain!.DestroySwapchain(_device, chain, null);
    }

    // ------------------------------------------------------------------ scenes

    public void CreateScenes(IReadOnlyList<string> names)
    {
        _sceneNames.AddRange(names);
        // Geometry
        var (verts, idx) = SceneMath.Sphere();
        _sphereVb = UploadBuffer(MemoryMarshal.AsBytes(verts.AsSpan()), BufferUsageFlags.VertexBufferBit);
        _sphereIb = UploadBuffer(MemoryMarshal.AsBytes(idx.AsSpan()), BufferUsageFlags.IndexBufferBit);
        _sphereIndexCount = (uint)idx.Length;
        // Particles
        var pdata = SceneMath.ParticleData();
        _particles = UploadBuffer(MemoryMarshal.AsBytes(pdata.AsSpan()), BufferUsageFlags.StorageBufferBit);
        // Fill texture with mips
        var mips = SceneMath.Mips(SceneMath.FillTexture(), SceneMath.FillTextureSize);
        (_fillTex, _fillMem) = CreateImage(SceneMath.FillTextureSize, SceneMath.FillTextureSize, (uint)mips.Count, Format.R8G8B8A8Unorm, ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit);
        UploadMips(_fillTex, mips, SceneMath.FillTextureSize);
        _fillView = CreateView(_fillTex, Format.R8G8B8A8Unorm, ImageAspectFlags.ColorBit, (uint)mips.Count);

        _sceneSet = AllocSet(_particles.Buf, _particles.Buf, _dummy.Buf, _dummy.Buf);

        var noBlend = Blend.None;
        _geoPipe = GraphicsPipeline("GeoVS", "GeoPS", depth: true, geoInput: true, noBlend);
        _shadePipe = GraphicsPipeline("FullVS", "ShadePS", depth: false, geoInput: false, noBlend);
        _partPipe = GraphicsPipeline("PartVS", "PartPS", depth: false, geoInput: false, Blend.Additive);
        _fillPipe = GraphicsPipeline("FullVS", "FillPS", depth: false, geoInput: false, Blend.Alpha);
        _simPipe = ComputePipeline("SimCS");
        _vramWritePipe = ComputePipeline("VramWriteCS");
        _vramVerifyPipe = ComputePipeline("VramVerifyCS");
    }

    private enum Blend { None, Additive, Alpha }

    private ShaderModule Module(string entry)
    {
        var code = ShaderSource.Spirv(entry);
        fixed (byte* p = code)
        {
            var ci = new ShaderModuleCreateInfo { SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)code.Length, PCode = (uint*)p };
            Check(_vk.CreateShaderModule(_device, in ci, null, out var m), "shader " + entry);
            return m;
        }
    }

    private Pipeline GraphicsPipeline(string vs, string ps, bool depth, bool geoInput, Blend blend)
    {
        var vsm = Module(vs); var psm = Module(ps);
        var main = (byte*)SilkMarshal.StringToPtr("main");
        try
        {
            var stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = vsm, PName = main };
            stages[1] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = psm, PName = main };
            var binding = new VertexInputBindingDescription(0, 12, VertexInputRate.Vertex);
            var attr = new VertexInputAttributeDescription(0, 0, Format.R32G32B32Sfloat, 0);
            var vi = new PipelineVertexInputStateCreateInfo { SType = StructureType.PipelineVertexInputStateCreateInfo };
            if (geoInput) { vi.VertexBindingDescriptionCount = 1; vi.PVertexBindingDescriptions = &binding; vi.VertexAttributeDescriptionCount = 1; vi.PVertexAttributeDescriptions = &attr; }
            var ia = new PipelineInputAssemblyStateCreateInfo { SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = PrimitiveTopology.TriangleList };
            var vp = new PipelineViewportStateCreateInfo { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };
            var rs = new PipelineRasterizationStateCreateInfo { SType = StructureType.PipelineRasterizationStateCreateInfo, PolygonMode = PolygonMode.Fill, CullMode = CullModeFlags.None, FrontFace = FrontFace.CounterClockwise, LineWidth = 1 };
            var ms = new PipelineMultisampleStateCreateInfo { SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = SampleCountFlags.Count1Bit };
            var ds = new PipelineDepthStencilStateCreateInfo { SType = StructureType.PipelineDepthStencilStateCreateInfo, DepthTestEnable = depth, DepthWriteEnable = depth, DepthCompareOp = CompareOp.Less };
            var cba = new PipelineColorBlendAttachmentState { ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit };
            if (blend == Blend.Additive) { cba.BlendEnable = true; cba.SrcColorBlendFactor = BlendFactor.One; cba.DstColorBlendFactor = BlendFactor.One; cba.ColorBlendOp = BlendOp.Add; cba.SrcAlphaBlendFactor = BlendFactor.One; cba.DstAlphaBlendFactor = BlendFactor.One; cba.AlphaBlendOp = BlendOp.Add; }
            if (blend == Blend.Alpha) { cba.BlendEnable = true; cba.SrcColorBlendFactor = BlendFactor.SrcAlpha; cba.DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha; cba.ColorBlendOp = BlendOp.Add; cba.SrcAlphaBlendFactor = BlendFactor.SrcAlpha; cba.DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha; cba.AlphaBlendOp = BlendOp.Add; }
            var cb = new PipelineColorBlendStateCreateInfo { SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = 1, PAttachments = &cba };
            var dyn = stackalloc DynamicState[] { DynamicState.Viewport, DynamicState.Scissor };
            var dsi = new PipelineDynamicStateCreateInfo { SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = dyn };
            var ci = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo, StageCount = 2, PStages = stages, PVertexInputState = &vi, PInputAssemblyState = &ia,
                PViewportState = &vp, PRasterizationState = &rs, PMultisampleState = &ms, PDepthStencilState = &ds, PColorBlendState = &cb, PDynamicState = &dsi,
                Layout = _pipeLayout, RenderPass = _scenePass, Subpass = 0,
            };
            Check(_vk.CreateGraphicsPipelines(_device, default, 1, in ci, null, out var pipe), $"pipeline {vs}/{ps}");
            return pipe;
        }
        finally
        {
            SilkMarshal.Free((nint)main);
            _vk.DestroyShaderModule(_device, vsm, null); _vk.DestroyShaderModule(_device, psm, null);
        }
    }

    private Pipeline ComputePipeline(string cs)
    {
        var m = Module(cs);
        var main = (byte*)SilkMarshal.StringToPtr("main");
        try
        {
            var ci = new ComputePipelineCreateInfo
            {
                SType = StructureType.ComputePipelineCreateInfo, Layout = _pipeLayout,
                Stage = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.ComputeBit, Module = m, PName = main },
            };
            Check(_vk.CreateComputePipelines(_device, default, 1, in ci, null, out var pipe), "compute pipeline " + cs);
            return pipe;
        }
        finally { SilkMarshal.Free((nint)main); _vk.DestroyShaderModule(_device, m, null); }
    }

    private DescriptorSet AllocSet(VkBuffer particlesIn, VkBuffer particlesRw, VkBuffer data, VkBuffer errors)
    {
        var layout = _setLayout;
        var ai = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = _descPool, DescriptorSetCount = 1, PSetLayouts = &layout };
        Check(_vk.AllocateDescriptorSets(_device, in ai, out var set), "descriptor set");
        var ubo = new DescriptorBufferInfo(_params.Buf, 0, (ulong)Unsafe.SizeOf<SceneParams>());
        var img = new DescriptorImageInfo(default, _fillView.Handle != 0 ? _fillView : _colorView, ImageLayout.ShaderReadOnlyOptimal);
        var smp = new DescriptorImageInfo(_sampler, default, ImageLayout.Undefined);
        var bufs = stackalloc DescriptorBufferInfo[4];
        bufs[0] = new DescriptorBufferInfo(particlesIn, 0, Vk.WholeSize);
        bufs[1] = new DescriptorBufferInfo(particlesRw, 0, Vk.WholeSize);
        bufs[2] = new DescriptorBufferInfo(data, 0, Vk.WholeSize);
        bufs[3] = new DescriptorBufferInfo(errors, 0, Vk.WholeSize);
        var w = stackalloc WriteDescriptorSet[7];
        w[0] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = 0, DescriptorCount = 1, DescriptorType = DescriptorType.UniformBufferDynamic, PBufferInfo = &ubo };
        w[1] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = 1, DescriptorCount = 1, DescriptorType = DescriptorType.SampledImage, PImageInfo = &img };
        w[2] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = 2, DescriptorCount = 1, DescriptorType = DescriptorType.Sampler, PImageInfo = &smp };
        for (int i = 0; i < 4; i++)
            w[3 + i] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = (uint)(3 + i), DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = &bufs[i] };
        _vk.UpdateDescriptorSets(_device, 7, w, 0, null);
        return set;
    }

    // ------------------------------------------------------------------ frames

    public void BeginFrame()
    {
        _frame = (_frame + 1) % FramesInFlight;
        var fence = _fences[_frame];
        _vk.WaitForFences(_device, 1, in fence, true, ulong.MaxValue);
        _haveImage = false;
        if (_khrSwapchain != null)
        {
            if (_swapchainDirty) CreateSwapchain();
            if (!_swapchainDirty)
            {
                var r = _khrSwapchain.AcquireNextImage(_device, _swapchain, ulong.MaxValue, _imageReady[_frame], default, ref _imageIndex);
                if (r == Result.ErrorOutOfDateKhr) _swapchainDirty = true;
                else if (r is Result.Success or Result.SuboptimalKhr) _haveImage = true;
                else Check(r, "acquire image");
            }
        }
        _vk.ResetFences(_device, 1, in fence);
        _drawSlot = 0;
        var cmd = _cmd[_frame];
        _vk.ResetCommandBuffer(cmd, 0);
        var bi = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
        _vk.BeginCommandBuffer(cmd, in bi);
    }

    private uint Param(in SceneParams p)
    {
        int slot = _frame * MaxDrawsPerFrame + Math.Min(_drawSlot++, MaxDrawsPerFrame - 1);
        ulong offset = (ulong)slot * _uboAlign;
        Unsafe.Write((void*)(_params.Ptr + (nint)offset), p);
        return (uint)offset;
    }

    public void RenderScene(int index, double time)
    {
        var cmd = _cmd[_frame];
        string name = _sceneNames[index];
        var set = _sceneSet;
        if (name == Core.Gpu.GpuScenes.Compute)
        {
            // 4 simulation steps before drawing.
            var p = SceneMath.Compute(time, _w, _h);
            _vk.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _simPipe);
            uint off = Param(p);
            _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _pipeLayout, 0, 1, &set, 1, &off);
            for (int s = 0; s < SceneMath.ComputeSteps; s++)
            {
                _vk.CmdDispatch(cmd, SceneMath.Particles / 256, 1, 1);
                var mb = new MemoryBarrier { SType = StructureType.MemoryBarrier, SrcAccessMask = AccessFlags.ShaderWriteBit, DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit };
                _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.VertexShaderBit, 0, 1, &mb, 0, null, 0, null);
            }
        }

        var clear = stackalloc ClearValue[2];
        clear[0] = new ClearValue(name switch
        {
            Core.Gpu.GpuScenes.Geometry => new ClearColorValue(0.02f, 0.03f, 0.05f, 1),
            Core.Gpu.GpuScenes.Compute => new ClearColorValue(0, 0, 0.01f, 1),
            _ => new ClearColorValue(0, 0, 0, 1),
        });
        clear[1] = new ClearValue(depthStencil: new ClearDepthStencilValue(1, 0));
        var rbi = new RenderPassBeginInfo { SType = StructureType.RenderPassBeginInfo, RenderPass = _scenePass, Framebuffer = _sceneFb, RenderArea = new Rect2D(default, new Extent2D((uint)_w, (uint)_h)), ClearValueCount = 2, PClearValues = clear };
        _vk.CmdBeginRenderPass(cmd, in rbi, SubpassContents.Inline);
        // Negative height: same orientation as Direct3D (y up in clip space).
        var viewport = new Viewport(0, _h, _w, -_h, 0, 1);
        _vk.CmdSetViewport(cmd, 0, 1, in viewport);
        var full = new Rect2D(default, new Extent2D((uint)_w, (uint)_h));
        _vk.CmdSetScissor(cmd, 0, 1, in full);

        switch (name)
        {
            case Core.Gpu.GpuScenes.Geometry:
            {
                _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _geoPipe);
                var vb = _sphereVb.Buf; ulong zero = 0;
                _vk.CmdBindVertexBuffers(cmd, 0, 1, &vb, &zero);
                _vk.CmdBindIndexBuffer(cmd, _sphereIb.Buf, 0, IndexType.Uint32);
                for (int b = 0; b < SceneMath.GeometryBatches; b++)
                {
                    uint off = Param(SceneMath.Geometry(time, _w, _h, b));
                    _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _pipeLayout, 0, 1, &set, 1, &off);
                    _vk.CmdDrawIndexed(cmd, _sphereIndexCount, SceneMath.GeometryInstances / SceneMath.GeometryBatches, 0, 0, 0);
                }
                break;
            }
            case Core.Gpu.GpuScenes.Shading:
            {
                _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _shadePipe);
                uint off = Param(SceneMath.Shading(time, _w, _h));
                _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _pipeLayout, 0, 1, &set, 1, &off);
                for (int i = 0; i < SceneMath.ShadingBands; i++)
                {
                    int y0 = _h * i / SceneMath.ShadingBands, y1 = _h * (i + 1) / SceneMath.ShadingBands;
                    var band = new Rect2D(new Offset2D(0, y0), new Extent2D((uint)_w, (uint)(y1 - y0)));
                    _vk.CmdSetScissor(cmd, 0, 1, in band);
                    _vk.CmdDraw(cmd, 3, 1, 0, 0);
                }
                break;
            }
            case Core.Gpu.GpuScenes.Compute:
            {
                _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _partPipe);
                uint off = Param(SceneMath.Compute(time, _w, _h));
                _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _pipeLayout, 0, 1, &set, 1, &off);
                _vk.CmdDraw(cmd, SceneMath.Particles * 6, 1, 0, 0);
                break;
            }
            default:
            {
                _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _fillPipe);
                for (int layer = 0; layer < SceneMath.FillLayers; layer++)
                {
                    uint off = Param(SceneMath.Fill(time, _w, _h, layer));
                    _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _pipeLayout, 0, 1, &set, 1, &off);
                    _vk.CmdDraw(cmd, 3, 1, 0, 0);
                }
                break;
            }
        }
        _vk.CmdEndRenderPass(cmd);
    }

    public void EndFrame(bool showScene, double overall, int sceneIndex, int sceneCount)
    {
        var cmd = _cmd[_frame];
        if (_haveImage)
        {
            var img = _swapImages[_imageIndex];
            Transition(cmd, img, ImageLayout.Undefined, ImageLayout.TransferDstOptimal, ImageAspectFlags.ColorBit);
            if (showScene)
            {
                var blit = new ImageBlit
                {
                    SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                    DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                };
                blit.SrcOffsets[1] = new Offset3D(_w, _h, 1);
                blit.DstOffsets[1] = new Offset3D((int)_swapExtent.Width, (int)_swapExtent.Height, 1);
                _vk.CmdBlitImage(cmd, _color, ImageLayout.TransferSrcOptimal, img, ImageLayout.TransferDstOptimal, 1, in blit, Filter.Linear);
            }
            else
            {
                var black = new ClearColorValue(0, 0, 0, 1);
                var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
                _vk.CmdClearColorImage(cmd, img, ImageLayout.TransferDstOptimal, in black, 1, in range);
            }
            Transition(cmd, img, ImageLayout.TransferDstOptimal, ImageLayout.ColorAttachmentOptimal, ImageAspectFlags.ColorBit);
            var rbi = new RenderPassBeginInfo { SType = StructureType.RenderPassBeginInfo, RenderPass = _overlayPass, Framebuffer = _swapFramebuffers[_imageIndex], RenderArea = new Rect2D(default, _swapExtent) };
            _vk.CmdBeginRenderPass(cmd, in rbi, SubpassContents.Inline);
            foreach (var (r, c) in SceneMath.Overlay(overall, sceneIndex, sceneCount, (int)_swapExtent.Height))
            {
                var ca = new ClearAttachment(ImageAspectFlags.ColorBit, 0, new ClearValue(new ClearColorValue(c.X, c.Y, c.Z, c.W)));
                int x = (int)(r.X * _swapExtent.Width), y = (int)(r.Y * _swapExtent.Height);
                uint w = (uint)Math.Max(1, Math.Min(r.Z * _swapExtent.Width, _swapExtent.Width - x)), h = (uint)Math.Max(1, Math.Min(r.W * _swapExtent.Height + 1, _swapExtent.Height - y));
                var cr = new ClearRect(new Rect2D(new Offset2D(x, y), new Extent2D(w, h)), 0, 1);
                _vk.CmdClearAttachments(cmd, 1, in ca, 1, in cr);
            }
            _vk.CmdEndRenderPass(cmd);
        }
        _vk.EndCommandBuffer(cmd);

        var waitStage = PipelineStageFlags.TransferBit;
        var imageReady = _imageReady[_frame]; var renderDone = _renderDone[_frame];
        var si = new SubmitInfo { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &cmd };
        if (_haveImage) { si.WaitSemaphoreCount = 1; si.PWaitSemaphores = &imageReady; si.PWaitDstStageMask = &waitStage; si.SignalSemaphoreCount = 1; si.PSignalSemaphores = &renderDone; }
        Check(_vk.QueueSubmit(_queue, 1, in si, _fences[_frame]), "submit");
        if (_haveImage)
        {
            var chain = _swapchain; var idx = _imageIndex;
            var pi = new PresentInfoKHR { SType = StructureType.PresentInfoKhr, WaitSemaphoreCount = 1, PWaitSemaphores = &renderDone, SwapchainCount = 1, PSwapchains = &chain, PImageIndices = &idx };
            var r = _khrSwapchain!.QueuePresent(_queue, in pi);
            if (r is Result.ErrorOutOfDateKhr or Result.SuboptimalKhr) _swapchainDirty = true;
            else if (r != Result.Success) Check(r, "present");
            var fb = _window!.FramebufferSize;
            if (fb.X != _swapExtent.Width || fb.Y != _swapExtent.Height) _swapchainDirty = true;
        }
    }

    private void Transition(CommandBuffer cmd, VkImage image, ImageLayout from, ImageLayout to, ImageAspectFlags aspect, uint mips = 1)
    {
        var b = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier, OldLayout = from, NewLayout = to, Image = image,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored, DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            SubresourceRange = new ImageSubresourceRange(aspect, 0, mips, 0, 1),
            SrcAccessMask = AccessFlags.MemoryWriteBit | AccessFlags.TransferWriteBit | AccessFlags.ColorAttachmentWriteBit,
            DstAccessMask = AccessFlags.MemoryReadBit | AccessFlags.TransferWriteBit | AccessFlags.TransferReadBit | AccessFlags.ColorAttachmentWriteBit | AccessFlags.ShaderReadBit,
        };
        _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.AllCommandsBit, 0, 0, null, 0, null, 1, &b);
    }

    // ------------------------------------------------------------------ VRAM, PCIe, screenshots

    public long InitVram(long targetBytes)
    {
        long chunk = 128L << 20, total = 0;
        while (total < targetBytes)
        {
            long bytes = Math.Min(chunk, targetBytes - total);
            try
            {
                var b = CreateBuffer(bytes, BufferUsageFlags.StorageBufferBit, MemoryPropertyFlags.DeviceLocalBit);
                var set = AllocSet(_dummy.Buf, _dummy.Buf, b.Buf, _errors.Buf);
                _vram.Add((b.Buf, b.Mem, bytes / 4, set));
                total += bytes;
            }
            catch { break; } // out of video memory: test what we got
        }
        return total;
    }

    public (long Errors, string? Detail) VramPass()
    {
        WaitIdle();
        _vramPass++;
        uint seed = _vramPass * 2654435761u;
        new Span<uint>((void*)_errors.Ptr, 4).Clear();
        OneShot(cmd =>
        {
            int slot = 0;
            foreach (var (_, _, words, set0) in _vram)
            {
                uint groups = (uint)Math.Min(65535, (words + 255) / 256);
                var p = new SceneParams { Seed = seed, Count = (uint)words, Base = groups * 256 };
                ulong offset = (ulong)((FramesInFlight * MaxDrawsPerFrame + Math.Min(slot++, 63)) * (long)_uboAlign);
                Unsafe.Write((void*)(_params.Ptr + (nint)offset), p);
                uint off = (uint)offset; var set = set0;
                var mb = new MemoryBarrier { SType = StructureType.MemoryBarrier, SrcAccessMask = AccessFlags.ShaderWriteBit, DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit | AccessFlags.HostReadBit };
                _vk.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _vramWritePipe);
                _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _pipeLayout, 0, 1, &set, 1, &off);
                _vk.CmdDispatch(cmd, groups, 1, 1);
                _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.ComputeShaderBit, 0, 1, &mb, 0, null, 0, null);
                _vk.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _vramVerifyPipe);
                _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _pipeLayout, 0, 1, &set, 1, &off);
                _vk.CmdDispatch(cmd, groups, 1, 1);
                _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.HostBit, 0, 1, &mb, 0, null, 0, null);
            }
        });
        var r = new ReadOnlySpan<uint>((void*)_errors.Ptr, 4);
        return r[0] == 0 ? (0, null) : (r[0], $"first bad word #{r[1]}: expected {r[2]:X8} read {r[3]:X8}");
    }

    public (double UploadGBs, double DownloadGBs) TransferTest(long bytes)
    {
        WaitIdle();
        var staging = CreateMappedBuffer(bytes, BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit);
        var gpu = CreateBuffer(bytes, BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit, MemoryPropertyFlags.DeviceLocalBit);
        try
        {
            new Random(3).NextBytes(new Span<byte>((void*)staging.Ptr, (int)Math.Min(bytes, int.MaxValue)));
            double up = 0, down = 0;
            var region = new BufferCopy(0, 0, (ulong)bytes);
            for (int pass = 0; pass < 3; pass++)
            {
                var sw = Stopwatch.StartNew();
                OneShot(cmd => _vk.CmdCopyBuffer(cmd, staging.Buf, gpu.Buf, 1, in region));
                up = Math.Max(up, bytes / sw.Elapsed.TotalSeconds / 1e9);
                sw.Restart();
                OneShot(cmd => _vk.CmdCopyBuffer(cmd, gpu.Buf, staging.Buf, 1, in region));
                down = Math.Max(down, bytes / sw.Elapsed.TotalSeconds / 1e9);
            }
            return (up, down);
        }
        finally
        {
            DestroyBuffer(staging.Buf, staging.Mem);
            DestroyBuffer(gpu.Buf, gpu.Mem);
        }
    }

    public byte[] ReadPixels()
    {
        WaitIdle();
        long bytes = (long)_w * _h * 4;
        var buf = CreateMappedBuffer(bytes, BufferUsageFlags.TransferDstBit);
        try
        {
            OneShot(cmd =>
            {
                var region = new BufferImageCopy { ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1), ImageExtent = new Extent3D((uint)_w, (uint)_h, 1) };
                _vk.CmdCopyImageToBuffer(cmd, _color, ImageLayout.TransferSrcOptimal, buf.Buf, 1, in region);
            });
            var px = new byte[bytes];
            new ReadOnlySpan<byte>((void*)buf.Ptr, (int)bytes).CopyTo(px);
            for (int i = 0; i < px.Length; i += 4) (px[i], px[i + 2]) = (px[i + 2], px[i]); // RGBA → BGRA
            return px;
        }
        finally { DestroyBuffer(buf.Buf, buf.Mem); }
    }

    public void WaitIdle()
    {
        if (_device.Handle != 0) _vk.DeviceWaitIdle(_device);
    }

    // ------------------------------------------------------------------ helpers

    private void OneShot(Action<CommandBuffer> record)
    {
        var ai = new CommandBufferAllocateInfo { SType = StructureType.CommandBufferAllocateInfo, CommandPool = _pool, Level = CommandBufferLevel.Primary, CommandBufferCount = 1 };
        _vk.AllocateCommandBuffers(_device, in ai, out var cmd);
        var bi = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
        _vk.BeginCommandBuffer(cmd, in bi);
        record(cmd);
        _vk.EndCommandBuffer(cmd);
        var fi = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
        _vk.CreateFence(_device, in fi, null, out var fence);
        var si = new SubmitInfo { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &cmd };
        Check(_vk.QueueSubmit(_queue, 1, in si, fence), "submit");
        _vk.WaitForFences(_device, 1, in fence, true, ulong.MaxValue);
        _vk.DestroyFence(_device, fence, null);
        _vk.FreeCommandBuffers(_device, _pool, 1, in cmd);
    }

    private uint MemoryType(uint bits, MemoryPropertyFlags flags)
    {
        for (int i = 0; i < _memProps.MemoryTypeCount; i++)
            if ((bits & (1u << i)) != 0 && (_memProps.MemoryTypes[i].PropertyFlags & flags) == flags) return (uint)i;
        throw new InvalidOperationException("no suitable memory type");
    }

    private (VkBuffer Buf, DeviceMemory Mem) CreateBuffer(long size, BufferUsageFlags usage, MemoryPropertyFlags props)
    {
        var ci = new BufferCreateInfo { SType = StructureType.BufferCreateInfo, Size = (ulong)size, Usage = usage, SharingMode = SharingMode.Exclusive };
        Check(_vk.CreateBuffer(_device, in ci, null, out var buf), "buffer");
        _vk.GetBufferMemoryRequirements(_device, buf, out var req);
        var ai = new MemoryAllocateInfo { SType = StructureType.MemoryAllocateInfo, AllocationSize = req.Size, MemoryTypeIndex = MemoryType(req.MemoryTypeBits, props) };
        var r = _vk.AllocateMemory(_device, in ai, null, out var mem);
        if (r != Result.Success) { _vk.DestroyBuffer(_device, buf, null); Check(r, "memory"); }
        _vk.BindBufferMemory(_device, buf, mem, 0);
        return (buf, mem);
    }

    private (VkBuffer Buf, DeviceMemory Mem, nint Ptr) CreateMappedBuffer(long size, BufferUsageFlags usage)
    {
        var (buf, mem) = CreateBuffer(size, usage, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        void* p;
        Check(_vk.MapMemory(_device, mem, 0, Vk.WholeSize, 0, &p), "map");
        return (buf, mem, (nint)p);
    }

    private void DestroyBuffer(VkBuffer b, DeviceMemory m)
    {
        _vk.DestroyBuffer(_device, b, null);
        _vk.FreeMemory(_device, m, null);
    }

    private (VkBuffer Buf, DeviceMemory Mem) UploadBuffer(ReadOnlySpan<byte> data, BufferUsageFlags usage)
    {
        var staging = CreateMappedBuffer(data.Length, BufferUsageFlags.TransferSrcBit);
        data.CopyTo(new Span<byte>((void*)staging.Ptr, data.Length));
        var dst = CreateBuffer(data.Length, usage | BufferUsageFlags.TransferDstBit, MemoryPropertyFlags.DeviceLocalBit);
        var region = new BufferCopy(0, 0, (ulong)data.Length);
        OneShot(cmd => _vk.CmdCopyBuffer(cmd, staging.Buf, dst.Buf, 1, in region));
        DestroyBuffer(staging.Buf, staging.Mem);
        return dst;
    }

    private (VkImage Image, DeviceMemory Mem) CreateImage(uint w, uint h, uint mips, Format format, ImageUsageFlags usage)
    {
        var ci = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo, ImageType = ImageType.Type2D, Format = format, Extent = new Extent3D(w, h, 1), MipLevels = mips, ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit, Tiling = ImageTiling.Optimal, Usage = usage, SharingMode = SharingMode.Exclusive, InitialLayout = ImageLayout.Undefined,
        };
        Check(_vk.CreateImage(_device, in ci, null, out var img), "image");
        _vk.GetImageMemoryRequirements(_device, img, out var req);
        var ai = new MemoryAllocateInfo { SType = StructureType.MemoryAllocateInfo, AllocationSize = req.Size, MemoryTypeIndex = MemoryType(req.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit) };
        Check(_vk.AllocateMemory(_device, in ai, null, out var mem), "image memory");
        _vk.BindImageMemory(_device, img, mem, 0);
        return (img, mem);
    }

    private ImageView CreateView(VkImage image, Format format, ImageAspectFlags aspect, uint mips)
    {
        var ci = new ImageViewCreateInfo { SType = StructureType.ImageViewCreateInfo, Image = image, ViewType = ImageViewType.Type2D, Format = format, SubresourceRange = new ImageSubresourceRange(aspect, 0, mips, 0, 1) };
        Check(_vk.CreateImageView(_device, in ci, null, out var v), "image view");
        return v;
    }

    private void UploadMips(VkImage image, List<uint[]> mips, int size)
    {
        long total = mips.Sum(m => (long)m.Length * 4);
        var staging = CreateMappedBuffer(total, BufferUsageFlags.TransferSrcBit);
        var regions = new BufferImageCopy[mips.Count];
        long offset = 0; int s = size;
        for (int i = 0; i < mips.Count; i++)
        {
            MemoryMarshal.AsBytes(mips[i].AsSpan()).CopyTo(new Span<byte>((void*)(staging.Ptr + (nint)offset), mips[i].Length * 4));
            regions[i] = new BufferImageCopy { BufferOffset = (ulong)offset, ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, (uint)i, 0, 1), ImageExtent = new Extent3D((uint)s, (uint)s, 1) };
            offset += mips[i].Length * 4L; s = Math.Max(1, s / 2);
        }
        OneShot(cmd =>
        {
            Transition(cmd, image, ImageLayout.Undefined, ImageLayout.TransferDstOptimal, ImageAspectFlags.ColorBit, (uint)mips.Count);
            fixed (BufferImageCopy* r = regions) _vk.CmdCopyBufferToImage(cmd, staging.Buf, image, ImageLayout.TransferDstOptimal, (uint)regions.Length, r);
            Transition(cmd, image, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal, ImageAspectFlags.ColorBit, (uint)mips.Count);
        });
        DestroyBuffer(staging.Buf, staging.Mem);
    }

    private static void Check(Result r, string what)
    {
        if (r != Result.Success) throw new InvalidOperationException($"Vulkan {what} failed: {r}");
    }

    public void Dispose()
    {
        if (_device.Handle == 0) return;
        _vk.DeviceWaitIdle(_device);
        foreach (var (b, m, _, _) in _vram) DestroyBuffer(b, m);
        foreach (var p in new[] { _geoPipe, _shadePipe, _partPipe, _fillPipe, _simPipe, _vramWritePipe, _vramVerifyPipe }) if (p.Handle != 0) _vk.DestroyPipeline(_device, p, null);
        if (_swapchain.Handle != 0) DestroySwapchainResources(_swapchain);
        _vk.DestroyDevice(_device, null);
        if (_surface.Handle != 0) _khrSurface?.DestroySurface(_instance, _surface, null);
        _vk.DestroyInstance(_instance, null);
        _device = default;
    }
}
