using System.IO.Compression;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace BenchForge.Core.Cpu;

/// <summary>
/// A deterministic CPU workload. RunUnit(seed) always returns the same checksum for the same seed on a healthy CPU,
/// which is how the stress test detects silent calculation errors.
/// </summary>
public abstract class CpuKernel
{
    public abstract string Name { get; }
    /// <summary>Display unit of throughput, e.g. "M numbers/s".</summary>
    public abstract string Unit { get; }
    /// <summary>Work done by one unit, in display units × 1 (throughput = units × WorkPerUnit / seconds).</summary>
    public abstract double WorkPerUnit { get; }
    public abstract bool IsInteger { get; }
    public abstract bool IsHeavyVector { get; }
    public abstract object CreateState();
    public abstract ulong RunUnit(object state, int seed);

    public static CpuKernel[] All() => new CpuKernel[]
    {
        new SieveKernel(), new NBodyKernel(), new MatrixFmaKernel(), new CryptoKernel(), new CompressionKernel(), new SortKernel(),
    };

    protected static ulong Mix(ulong h, ulong v)
    {
        h ^= v + 0x9E3779B97F4A7C15UL + (h << 6) + (h >> 2);
        return h * 0xBF58476D1CE4E5B9UL;
    }

    protected static ulong XorShift(ref ulong s)
    {
        s ^= s << 13; s ^= s >> 7; s ^= s << 17;
        return s;
    }
}

public sealed class SieveKernel : CpuKernel
{
    private const int Limit = 1_000_000;
    public override string Name => "Integer (prime sieve)";
    public override string Unit => "M numbers/s";
    public override double WorkPerUnit => Limit / 1e6;
    public override bool IsInteger => true;
    public override bool IsHeavyVector => false;
    public override object CreateState() => new byte[Limit + 64];

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public override ulong RunUnit(object state, int seed)
    {
        var composite = (byte[])state;
        int n = Limit + (seed & 63);
        Array.Clear(composite, 0, n + 1);
        for (long i = 2; i * i <= n; i++)
            if (composite[i] == 0)
                for (long j = i * i; j <= n; j += i) composite[j] = 1;
        ulong count = 0, h = 1469598103934665603UL;
        for (int i = 2; i <= n; i++)
            if (composite[i] == 0) { count++; if ((count & 1023) == 0) h = Mix(h, (ulong)i); }
        return Mix(h, count);
    }
}

public sealed class NBodyKernel : CpuKernel
{
    private const int Bodies = 96, Steps = 24;
    public override string Name => "Floating point (n-body)";
    public override string Unit => "M interactions/s";
    public override double WorkPerUnit => Bodies * (Bodies - 1) / 2.0 * Steps / 1e6;
    public override bool IsInteger => false;
    public override bool IsHeavyVector => false;
    public override object CreateState() => new double[Bodies * 7];

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public override ulong RunUnit(object state, int seed)
    {
        var b = (double[])state; // x y z vx vy vz m
        ulong s = 0x2545F4914F6CDD1DUL ^ (ulong)(seed + 1) * 0x9E3779B97F4A7C15UL;
        for (int i = 0; i < Bodies; i++)
        {
            for (int k = 0; k < 6; k++) b[i * 7 + k] = (XorShift(ref s) % 20000) / 1000.0 - 10.0;
            b[i * 7 + 6] = 1.0 + (XorShift(ref s) % 1000) / 500.0;
        }
        const double dt = 0.001, soft = 0.01;
        for (int step = 0; step < Steps; step++)
        {
            for (int i = 0; i < Bodies; i++)
            {
                int ii = i * 7;
                double xi = b[ii], yi = b[ii + 1], zi = b[ii + 2], mi = b[ii + 6];
                double vxi = 0, vyi = 0, vzi = 0;
                for (int j = i + 1; j < Bodies; j++)
                {
                    int jj = j * 7;
                    double dx = b[jj] - xi, dy = b[jj + 1] - yi, dz = b[jj + 2] - zi;
                    double d2 = dx * dx + dy * dy + dz * dz + soft;
                    double inv = 1.0 / (d2 * Math.Sqrt(d2));
                    double fi = b[jj + 6] * inv * dt, fj = mi * inv * dt;
                    vxi += dx * fi; vyi += dy * fi; vzi += dz * fi;
                    b[jj + 3] -= dx * fj; b[jj + 4] -= dy * fj; b[jj + 5] -= dz * fj;
                }
                b[ii + 3] += vxi; b[ii + 4] += vyi; b[ii + 5] += vzi;
            }
            for (int i = 0; i < Bodies; i++)
            {
                int ii = i * 7;
                b[ii] += b[ii + 3] * dt; b[ii + 1] += b[ii + 4] * dt; b[ii + 2] += b[ii + 5] * dt;
            }
        }
        ulong h = 0xCBF29CE484222325UL;
        for (int i = 0; i < Bodies * 7; i++) h = Mix(h, (ulong)BitConverter.DoubleToInt64Bits(b[i]));
        return h;
    }
}

/// <summary>Single-precision matrix multiply with fused multiply-add on the widest vectors the CPU supports (AVX2/AVX-512). Hottest workload.</summary>
public sealed class MatrixFmaKernel : CpuKernel
{
    private const int N = 128;
    public override string Name => "Vector FMA (matrix)";
    public override string Unit => "GFLOP/s";
    public override double WorkPerUnit => 2.0 * N * N * N / 1e9;
    public override bool IsInteger => false;
    public override bool IsHeavyVector => true;

    private sealed class State { public float[] A = new float[N * N], B = new float[N * N], C = new float[N * N]; }
    public override object CreateState() => new State();

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public override ulong RunUnit(object state, int seed)
    {
        var st = (State)state;
        ulong s = 0x9E3779B97F4A7C15UL * (ulong)(seed + 7);
        for (int i = 0; i < N * N; i++)
        {
            st.A[i] = (XorShift(ref s) % 2000) / 1000f - 1f;
            st.B[i] = (XorShift(ref s) % 2000) / 1000f - 1f;
            st.C[i] = 0;
        }
        int w = Vector<float>.Count;
        var a = st.A; var bm = st.B; var c = st.C;
        for (int i = 0; i < N; i++)
        {
            var cRow = c.AsSpan(i * N, N);
            for (int k = 0; k < N; k++)
            {
                var aik = new Vector<float>(a[i * N + k]);
                var bRow = bm.AsSpan(k * N, N);
                for (int j = 0; j < N; j += w)
                {
                    var cv = new Vector<float>(cRow.Slice(j));
                    var bv = new Vector<float>(bRow.Slice(j));
                    Vector.FusedMultiplyAdd(aik, bv, cv).CopyTo(cRow.Slice(j));
                }
            }
        }
        ulong h = 0x84222325CBF29CE4UL;
        for (int i = 0; i < N * N; i += 7) h = Mix(h, BitConverter.SingleToUInt32Bits(c[i]));
        return h;
    }
}

public sealed class CryptoKernel : CpuKernel
{
    private const int Size = 64 * 1024;
    public override string Name => "Crypto (AES-GCM + SHA-256)";
    public override string Unit => "MB/s";
    public override double WorkPerUnit => 2.0 * Size / 1e6;
    public override bool IsInteger => true;
    public override bool IsHeavyVector => false;

    private sealed class State
    {
        public byte[] Plain = new byte[Size], Cipher = new byte[Size], Tag = new byte[16], Hash = new byte[32], Nonce = new byte[12];
        public Dictionary<int, AesGcm> Aes = new();
    }
    public override object CreateState() => new State();

    public override ulong RunUnit(object state, int seed)
    {
        var st = (State)state;
        if (!st.Aes.TryGetValue(seed & 7, out var aes))
        {
            var key = new byte[32];
            for (int i = 0; i < 32; i++) key[i] = (byte)(i * 31 + seed * 7);
            st.Aes[seed & 7] = aes = new AesGcm(key, 16);
        }
        ulong s = 0xA5A5A5A5DEADBEEFUL ^ (ulong)seed;
        var span = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(st.Plain.AsSpan());
        for (int i = 0; i < span.Length; i++) span[i] = XorShift(ref s);
        BitConverter.TryWriteBytes(st.Nonce, seed);
        aes.Encrypt(st.Nonce, st.Plain, st.Cipher, st.Tag);
        SHA256.HashData(st.Cipher, st.Hash);
        return BitConverter.ToUInt64(st.Hash, 0) ^ BitConverter.ToUInt64(st.Tag, 0);
    }
}

public sealed class CompressionKernel : CpuKernel
{
    private const int Size = 256 * 1024;
    public override string Name => "Compression (Brotli)";
    public override string Unit => "MB/s";
    public override double WorkPerUnit => Size / 1e6;
    public override bool IsInteger => true;
    public override bool IsHeavyVector => false;

    private sealed class State { public byte[] Input = new byte[Size]; public byte[] Output = new byte[Size + 4096]; public int LastSeed = -1; }
    public override object CreateState() => new State();

    private static readonly string[] Words = "the quick brown fox jumps over lazy dog benchmark stress memory processor cache thread voltage temperature clock driver kernel error stable result score".Split(' ');

    public override ulong RunUnit(object state, int seed)
    {
        var st = (State)state;
        if (st.LastSeed != seed)
        {
            // Text-like, semi-compressible data.
            ulong s = 0x1234567UL + (ulong)seed * 977;
            int p = 0;
            while (p < Size)
            {
                var w = Words[XorShift(ref s) % (ulong)Words.Length];
                for (int i = 0; i < w.Length && p < Size; i++) st.Input[p++] = (byte)w[i];
                if (p < Size) st.Input[p++] = (XorShift(ref s) % 11) == 0 ? (byte)('0' + (int)(s % 10)) : (byte)' ';
            }
            st.LastSeed = seed;
        }
        using var enc = new BrotliEncoder(quality: 4, window: 22);
        enc.Compress(st.Input, st.Output, out _, out int written, isFinalBlock: true);
        ulong h = (ulong)written;
        for (int i = 0; i < written; i += 64) h = Mix(h, st.Output[i]);
        return h;
    }
}

public sealed class SortKernel : CpuKernel
{
    private const int Count = 64 * 1024;
    public override string Name => "Sorting";
    public override string Unit => "M elements/s";
    public override double WorkPerUnit => Count / 1e6;
    public override bool IsInteger => true;
    public override bool IsHeavyVector => false;
    public override object CreateState() => new int[Count];

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public override ulong RunUnit(object state, int seed)
    {
        var arr = (int[])state;
        ulong s = 0xFEEDFACECAFEBEEFUL ^ ((ulong)seed << 32 | (uint)seed);
        for (int i = 0; i < Count; i++) arr[i] = (int)XorShift(ref s);
        Array.Sort(arr);
        ulong h = 0;
        for (int i = 0; i < Count; i += 97) h = Mix(h, (ulong)(uint)arr[i]);
        return h;
    }
}
