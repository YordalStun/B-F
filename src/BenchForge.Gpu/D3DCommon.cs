using System.Runtime.Versioning;
using System.Text;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;

namespace BenchForge.Gpu;

/// <summary>Shared Direct3D 11/12 helpers: HLSL compilation with the D3DCompiler that ships with Windows.</summary>
[SupportedOSPlatform("windows")]
internal static unsafe class D3DCommon
{
    private static D3DCompiler? _compiler;
    private static readonly Dictionary<string, byte[]> Cache = new();

    /// <summary>Compiles one entry point of scenes.hlsl (shader model 5.0: works on Direct3D 11 and 12).</summary>
    public static byte[] Compile(string entry, string target)
    {
        lock (Cache)
        {
            var key = entry + "/" + target;
            if (Cache.TryGetValue(key, out var hit)) return hit;
            _compiler ??= D3DCompiler.GetApi();
            var src = Encoding.UTF8.GetBytes(ShaderSource.Hlsl());
            ID3D10Blob* code = null, errors = null;
            var name = (byte*)SilkMarshal.StringToPtr("scenes.hlsl");
            var ep = (byte*)SilkMarshal.StringToPtr(entry);
            var tg = (byte*)SilkMarshal.StringToPtr(target);
            try
            {
                const uint OptimizationLevel3 = 1 << 15;
                int hr;
                fixed (byte* s = src) hr = _compiler.Compile(s, (nuint)src.Length, name, null, null, ep, tg, OptimizationLevel3, 0, &code, &errors);
                if (hr < 0)
                {
                    string msg = errors != null ? Encoding.UTF8.GetString((byte*)errors->GetBufferPointer(), (int)errors->GetBufferSize()) : $"HRESULT 0x{hr:X8}";
                    throw new InvalidOperationException($"HLSL {entry} ({target}) failed to compile: {msg}");
                }
                var bytes = new ReadOnlySpan<byte>(code->GetBufferPointer(), (int)code->GetBufferSize()).ToArray();
                Cache[key] = bytes;
                return bytes;
            }
            finally
            {
                if (code != null) code->Release();
                if (errors != null) errors->Release();
                SilkMarshal.Free((nint)name); SilkMarshal.Free((nint)ep); SilkMarshal.Free((nint)tg);
            }
        }
    }

    /// <summary>Set by the active backend: asks the device why it was lost.</summary>
    public static Func<int>? RemovedReason;

    public static void Check(int hr, string what)
    {
        if (hr >= 0) return;
        string why = "";
        if ((uint)hr is 0x887A0005 or 0x887A0006 or 0x887A0007 && RemovedReason != null)
        {
            int r = 0;
            try { r = RemovedReason(); } catch { }
            why = (uint)r switch
            {
                0x887A0006 => " - the GPU stopped responding (driver timeout)",
                0x887A0007 => " - the GPU was reset",
                0x887A0020 => " - driver internal error",
                0x887A0001 => " - the program sent the GPU an invalid command",
                0x887A0005 => " - the GPU was removed",
                _ => r < 0 ? $" - reason 0x{r:X8}" : "",
            };
        }
        throw new InvalidOperationException($"{what} failed (HRESULT 0x{hr:X8}){why}");
    }
}
