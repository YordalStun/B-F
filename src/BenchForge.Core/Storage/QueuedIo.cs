using System.Buffers;
using System.Diagnostics;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BenchForge.Core.Storage;

/// <summary>Page-aligned native buffer usable as Memory&lt;byte&gt; for unbuffered async I/O.</summary>
internal sealed unsafe class NativeBuffer : MemoryManager<byte>
{
    private readonly byte* _ptr;
    private readonly int _length;
    public NativeBuffer(int length) { _length = length; _ptr = (byte*)NativeMemory.AlignedAlloc((nuint)length, 4096); }
    public byte* Pointer => _ptr;
    public override Span<byte> GetSpan() => new(_ptr, _length);
    public override MemoryHandle Pin(int elementIndex = 0) => new(_ptr + elementIndex);
    public override void Unpin() { }
    protected override void Dispose(bool disposing) => NativeMemory.AlignedFree(_ptr);
}

/// <summary>Latency percentiles of one test (microseconds).</summary>
public sealed record LatencyStats(double AvgUs, double P99Us, double P999Us, double MaxUs, int Stalls, long Samples)
{
    public static LatencyStats? From(List<long> ticks)
    {
        if (ticks.Count == 0) return null;
        ticks.Sort();
        double toUs = 1e6 / Stopwatch.Frequency;
        double At(double q) => ticks[Math.Min(ticks.Count - 1, (int)(ticks.Count * q))] * toUs;
        long stallTicks = (long)(0.050 * Stopwatch.Frequency);
        return new LatencyStats(ticks.Average() * toUs, At(0.99), At(0.999), ticks[^1] * toUs, ticks.Count(t => t >= stallTicks), ticks.Count);
    }

    public static string Format(double us) => us >= 1000 ? $"{us / 1000:0.0} ms" : $"{us:0} µs";
    public override string ToString() => $"avg {Format(AvgUs)} · 99% {Format(P99Us)} · 99.9% {Format(P999Us)} · max {Format(MaxUs)}" + (Stalls > 0 ? $" · {Stalls} stalls over 50 ms" : "");
}

/// <summary>Options for one queued I/O test.</summary>
internal sealed class QueuedTest
{
    public int BlockSize = 4096;
    public int QueueDepth = 1;
    public int Threads = 1;
    public bool Sequential;
    /// <summary>0 = all writes, 1 = all reads, 0.7 = 70/30 mixed.</summary>
    public double ReadFraction = 1;
    public double Seconds = 5;
    public bool Latency;
    /// <summary>Sequential only: stop after one pass over the area (used for the first write that fills the file).</summary>
    public bool SinglePass;
    public ulong[]? Hashes;       // sequential write records, sequential read verifies
    public ulong Seed = 1;
}

internal sealed record QueuedResult(double MBs, long Bytes, LatencyStats? Latency, int Mismatches, double Seconds);

/// <summary>
/// Runs reads/writes with a real queue depth (many I/Os in flight per thread via async/overlapped I/O),
/// like CrystalDiskMark's Q32T1 / Q8T1 tests.
/// </summary>
internal static class QueuedIo
{
    public static async Task<QueuedResult> RunAsync(SafeFileHandle h, long area, QueuedTest t, CancellationToken ct, Action<double>? liveMBs = null, Action<double, long>? progress = null)
    {
        long blocks = Math.Max(1, area / t.BlockSize);
        long nextSeq = -1, done = 0;
        int mismatches = 0;
        var latencies = t.Latency ? new List<long>[t.Threads * t.QueueDepth] : null;
        long stopAt = Stopwatch.GetTimestamp() + (long)(t.Seconds * Stopwatch.Frequency);
        long start = Stopwatch.GetTimestamp();
        int workers = t.Threads * t.QueueDepth;

        async Task Worker(int id)
        {
            using var buf = new NativeBuffer(t.BlockSize);
            var mem = buf.Memory;
            var rng = new Random(unchecked((int)(t.Seed * 7919) + id * 104729));
            var lat = t.Latency ? (latencies![id] = new List<long>(1 << 16)) : null;
            if (t.ReadFraction < 1) FillBlock(buf.GetSpan(), t.Seed * 1_000_003 + (ulong)id);
            while (Stopwatch.GetTimestamp() < stopAt && !ct.IsCancellationRequested)
            {
                long b;
                if (t.Sequential)
                {
                    b = Interlocked.Increment(ref nextSeq);
                    if (b >= blocks) { if (t.SinglePass) break; b %= blocks; }
                }
                else b = rng.NextInt64(blocks);
                long off = b * t.BlockSize;
                bool read = t.ReadFraction >= 1 || (t.ReadFraction > 0 && rng.NextDouble() < t.ReadFraction);
                long t0 = Stopwatch.GetTimestamp();
                if (read)
                {
                    int n = await RandomAccess.ReadAsync(h, mem, off, ct);
                    if (t.Hashes != null && b < t.Hashes.Length && (n != t.BlockSize || XxHash3.HashToUInt64(buf.GetSpan()) != t.Hashes[b]))
                        Interlocked.Increment(ref mismatches);
                }
                else
                {
                    if (t.Hashes != null)
                    {
                        FillBlock(buf.GetSpan(), t.Seed * 1_000_003 + (ulong)b);
                        if (b < t.Hashes.Length) t.Hashes[b] = XxHash3.HashToUInt64(buf.GetSpan());
                    }
                    await RandomAccess.WriteAsync(h, (ReadOnlyMemory<byte>)mem, off, ct);
                }
                lat?.Add(Stopwatch.GetTimestamp() - t0);
                Interlocked.Add(ref done, t.BlockSize);
            }
        }

        var tasks = Enumerable.Range(0, workers).Select(i => Task.Run(() => Worker(i), CancellationToken.None)).ToList();
        // Live speed while running.
        long last = 0; var sw = Stopwatch.StartNew(); double lastT = 0;
        while (!Task.WhenAll(tasks).IsCompleted)
        {
            await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(250, CancellationToken.None));
            double now = sw.Elapsed.TotalSeconds;
            if (now - lastT >= 0.9)
            {
                long d = Interlocked.Read(ref done);
                double mbs = (d - last) / 1e6 / (now - lastT);
                liveMBs?.Invoke(mbs); progress?.Invoke(mbs, d);
                last = d; lastT = now;
            }
        }
        try { await Task.WhenAll(tasks); } catch (OperationCanceledException) { }
        double elapsed = Math.Max(1e-6, (Stopwatch.GetTimestamp() - start) / (double)Stopwatch.Frequency);
        LatencyStats? stats = latencies == null ? null : LatencyStats.From(latencies.Where(l => l != null).SelectMany(l => l).ToList());
        return new QueuedResult(done / 1e6 / elapsed, done, stats, mismatches, elapsed);
    }

    internal static void FillBlock(Span<byte> bytes, ulong seed)
    {
        ulong s = seed * 0x9E3779B97F4A7C15UL | 1;
        var span = MemoryMarshal.Cast<byte, ulong>(bytes);
        for (int i = 0; i < span.Length; i++) { s ^= s << 13; s ^= s >> 7; s ^= s << 17; span[i] = s; }
    }
}
