using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BenchForge.Core.Engine;
using BenchForge.Core.Model;
using BenchForge.Core.Platform;
using BenchForge.Core.Scoring;

namespace BenchForge.Core.Memory;

/// <summary>RAM bandwidth/latency benchmark and RAM error test (memtest-style patterns over most of the free memory).</summary>
public sealed unsafe class MemoryModule : ITestModule
{
    public Component Component => Component.Memory;

    public Task<ModuleResult> RunAsync(StepContext ctx) =>
        Task.FromResult(ctx.Kind == StepKind.Benchmark ? Benchmark(ctx) : Stress(ctx));

    // ---------------------------------------------------------------- benchmark

    private ModuleResult Benchmark(StepContext ctx)
    {
        var result = new ModuleResult { Component = Component.Memory, Kind = StepKind.Benchmark };
        var (total, avail) = MemoryStatus.Get();
        long size = (long)Math.Min(1024L << 20, avail / 4);
        size = Math.Max(64L << 20, size) & ~((4L << 20) - 1);
        int threads = Environment.ProcessorCount;
        result.Info["Test buffer"] = $"{size >> 20} MB";
        result.Info["Installed RAM"] = $"{total / 1073741824.0:0.0} GB";
        double phase = Math.Max(0.5, (ctx.DurationSeconds - 3) / 4.0);

        byte* buf = (byte*)NativeMemory.AlignedAlloc((nuint)size, 4096);
        try
        {
            ctx.Progress("Preparing buffer", 0);
            Parallel.For(0, threads, t =>
            {
                long chunk = size / threads;
                new Span<byte>(buf + t * chunk, (int)Math.Min(chunk, int.MaxValue)).Fill((byte)t);
            });

            ctx.Progress("Read bandwidth", 0.05);
            double read = Bandwidth(ctx, buf, size, threads, phase, Op.Read);
            ctx.Progress("Write bandwidth", 0.3);
            double write = Bandwidth(ctx, buf, size, threads, phase, Op.Write);
            ctx.Progress("Copy bandwidth", 0.55);
            double copy = Bandwidth(ctx, buf, size, threads, phase, Op.Copy);
            ctx.Progress("Latency", 0.8);
            double latency = Latency(ctx, buf, Math.Min(size, 256L << 20), phase);

            void Add(string name, double v, string unit, double reference, bool higher = true) =>
                result.SubScores.Add(new SubScore { Name = name, Group = "Memory", Value = Math.Round(v, 1), Unit = unit, Score = Math.Round(Score.Linear(v, reference, higher)), HigherIsBetter = higher });
            Add("Read bandwidth", read, "MB/s", ScoreReferences.MemReadMBs);
            Add("Write bandwidth", write, "MB/s", ScoreReferences.MemWriteMBs);
            Add("Copy bandwidth", copy, "MB/s", ScoreReferences.MemCopyMBs);
            Add("Latency", latency, "ns", ScoreReferences.MemLatencyNs, higher: false);
            if (!ctx.Token.IsCancellationRequested)
                result.Score = Math.Round(Score.GeoMean(result.SubScores.Select(s => s.Score)));
        }
        finally
        {
            NativeMemory.AlignedFree(buf);
        }
        ctx.Progress("Done", 1);
        return result;
    }

    private enum Op { Read, Write, Copy }

    private static double Bandwidth(StepContext ctx, byte* buf, long size, int threads, double seconds, Op op)
    {
        long chunk = (size / threads) & ~4095L;
        long[] bytes = new long[threads];
        var stopAt = Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency);
        var start = Stopwatch.GetTimestamp();
        var workers = Enumerable.Range(0, threads).Select(t => new Thread(() =>
        {
            byte* p = buf + t * chunk;
            long n = 0;
            ulong sink = 0;
            while (Stopwatch.GetTimestamp() < stopAt && !ctx.Token.IsCancellationRequested)
            {
                switch (op)
                {
                    case Op.Read: sink += ReadSum(p, chunk); n += chunk; break;
                    case Op.Write: WriteFill(p, chunk, (ulong)n); n += chunk; break;
                    case Op.Copy:
                        long half = chunk / 2 & ~63L;
                        Buffer.MemoryCopy(p, p + half, half, half);
                        n += half * 2; // read + write traffic
                        break;
                }
            }
            bytes[t] = n + (long)(sink & 0);
        }) { IsBackground = true, Priority = ThreadPriority.BelowNormal }).ToList();
        workers.ForEach(w => w.Start());
        workers.ForEach(w => w.Join());
        double elapsed = (Stopwatch.GetTimestamp() - start) / (double)Stopwatch.Frequency;
        double mbs = bytes.Sum() / 1e6 / elapsed;
        ctx.Metric("mem.bw", $"RAM {op.ToString().ToLower()} bandwidth", Math.Round(mbs), "MB/s");
        return mbs;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static ulong ReadSum(byte* p, long len)
    {
        var v0 = Vector<ulong>.Zero; var v1 = Vector<ulong>.Zero; var v2 = Vector<ulong>.Zero; var v3 = Vector<ulong>.Zero;
        int w = Vector<byte>.Count;
        byte* end = p + len - 4 * w;
        for (; p <= end; p += 4 * w)
        {
            v0 += Unsafe.ReadUnaligned<Vector<ulong>>(p);
            v1 += Unsafe.ReadUnaligned<Vector<ulong>>(p + w);
            v2 += Unsafe.ReadUnaligned<Vector<ulong>>(p + 2 * w);
            v3 += Unsafe.ReadUnaligned<Vector<ulong>>(p + 3 * w);
        }
        return Vector.Sum(v0 + v1 + v2 + v3);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void WriteFill(byte* p, long len, ulong value)
    {
        var v = new Vector<ulong>(value);
        int w = Vector<byte>.Count;
        byte* end = p + len - 4 * w;
        for (; p <= end; p += 4 * w)
        {
            Unsafe.WriteUnaligned(p, v);
            Unsafe.WriteUnaligned(p + w, v);
            Unsafe.WriteUnaligned(p + 2 * w, v);
            Unsafe.WriteUnaligned(p + 3 * w, v);
        }
    }

    /// <summary>Random pointer chase across a large buffer: average time per dependent load (ns).</summary>
    private static double Latency(StepContext ctx, byte* buf, long size, double seconds)
    {
        long nodes = size / 64;
        long* idx = (long*)buf;
        // Sattolo's algorithm: a single random cycle visiting every cache line.
        var perm = new int[nodes];
        for (int i = 0; i < nodes; i++) perm[i] = i;
        var rng = new Random(42);
        for (int i = (int)nodes - 1; i > 0; i--) { int j = rng.Next(i); (perm[i], perm[j]) = (perm[j], perm[i]); }
        for (int i = 0; i < nodes; i++) idx[(long)perm[i] * 8] = (long)perm[(i + 1) % nodes] * 8;

        long cur = 0, steps = 0;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds && !ctx.Token.IsCancellationRequested)
        {
            for (int i = 0; i < 100_000; i++) cur = idx[cur];
            steps += 100_000;
        }
        double ns = sw.Elapsed.TotalMilliseconds * 1e6 / Math.Max(1, steps);
        ctx.Metric("mem.lat", "RAM latency", Math.Round(ns + (cur & 0), 1), "ns");
        return ns;
    }

    // ---------------------------------------------------------------- stress / error test

    private const long BlockSize = 64L << 20;

    private ModuleResult Stress(StepContext ctx)
    {
        var result = new ModuleResult { Component = Component.Memory, Kind = StepKind.Stress };
        var (total, avail) = MemoryStatus.Get();
        double pct = Math.Clamp(ctx.Settings.MemoryPercent, 5, 90) / 100.0;
        // Stay well clear of the page file: leave at least 1 GB (and other stress tests running alongside need room too).
        long reserve = (1L << 30) + (ctx.Concurrent ? (1L << 30) : 0);
        long target = (long)Math.Max(BlockSize, Math.Min(avail * pct, (double)avail - reserve));
        int blockCount = (int)Math.Max(1, target / BlockSize);
        ctx.Progress($"Allocating {blockCount * (BlockSize >> 20)} MB", 0);

        var blocks = new List<nint>();
        try
        {
            for (int i = 0; i < blockCount && !ctx.Token.IsCancellationRequested; i++)
            {
                try { blocks.Add((nint)NativeMemory.AlignedAlloc((nuint)BlockSize, 4096)); }
                catch (OutOfMemoryException) { break; }
            }
            long testedBytes = blocks.Count * BlockSize;
            result.Info["Memory under test"] = $"{testedBytes >> 20} MB of {total >> 20} MB installed";
            int threads = Math.Max(1, Math.Min(Environment.ProcessorCount - (ctx.Concurrent ? Environment.ProcessorCount / 2 : 0), blocks.Count));
            result.Info["Threads"] = threads.ToString();

            long verified = 0; int errors = 0; int passes = 0;
            var bitHistogram = new int[64];
            var deadline = ctx.Deadline;
            var workers = Enumerable.Range(0, threads).Select(t => new Thread(() =>
            {
                int pass = 0;
                while (DateTime.Now < deadline && !ctx.Token.IsCancellationRequested)
                {
                    var pattern = (Pattern)(pass % 4);
                    for (int b = t; b < blocks.Count && DateTime.Now < deadline && !ctx.Token.IsCancellationRequested; b += threads)
                    {
                        var p = (ulong*)blocks[b];
                        ulong seed = (ulong)(pass * 7919 + b * 104729 + 1);
                        int e = TestBlock(p, BlockSize / 8, pattern, seed, (offset, expected, actual) =>
                        {
                            ulong diff = expected ^ actual;
                            for (int bit = 0; bit < 64; bit++) if ((diff >> bit & 1) != 0) Interlocked.Increment(ref bitHistogram[bit]);
                            ctx.Error(new TestError
                            {
                                Test = $"RAM {pattern}", Thread = t,
                                Message = $"Memory error in block {b} at +0x{offset * 8:X}",
                                Detail = $"expected {expected:X16} read {actual:X16} (bits {diff:X16})",
                            });
                        });
                        Interlocked.Add(ref errors, e);
                        Interlocked.Add(ref verified, BlockSize);
                    }
                    pass++;
                    if (t == 0) Interlocked.Increment(ref passes);
                }
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal }).ToList();
            workers.ForEach(w => w.Start());
            long last = 0; var lastT = DateTime.Now;
            while (workers.Any(w => w.IsAlive))
            {
                Thread.Sleep(500);
                var now = DateTime.Now; long v = Interlocked.Read(ref verified);
                double dt = (now - lastT).TotalSeconds;
                if (dt >= 0.9) { ctx.Metric("mem.verify", "RAM test speed", Math.Round((v - last) / 1e6 / dt), "MB/s"); last = v; lastT = now; }
                ctx.Progress($"Pattern test over {testedBytes >> 20} MB — pass {passes + 1}", ctx.Elapsed.TotalSeconds / ctx.DurationSeconds);
            }
            result.ErrorCount = errors;
            result.Info["Data verified"] = $"{verified / 1e9:0.0} GB ({passes} full passes)";
            result.SubScores.Add(new SubScore { Name = "Memory tested", Group = "Stress", Value = testedBytes >> 20, Unit = "MB" });
            result.SubScores.Add(new SubScore { Name = "Errors", Group = "Stress", Value = errors, Unit = "", HigherIsBetter = false });
            if (errors > 0)
                result.Info["Failing bit positions"] = string.Join(", ", bitHistogram.Select((c, i) => (c, i)).Where(x => x.c > 0).OrderByDescending(x => x.c).Take(8).Select(x => $"bit {x.i} ×{x.c}"));
        }
        finally
        {
            foreach (var b in blocks) NativeMemory.AlignedFree((void*)b);
        }
        ctx.Progress("Done", 1);
        return result;
    }

    public enum Pattern { AddressInAddress, Checkerboard, WalkingOnes, Random }

    /// <summary>Writes a pattern to a block, reads it back and returns the number of mismatching 64-bit words.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static int TestBlock(ulong* p, long words, Pattern pattern, ulong seed, Action<long, ulong, ulong>? onError)
    {
        for (long i = 0; i < words; i++) p[i] = Expected(pattern, (ulong)p, i, seed);
        int errors = 0;
        for (long i = 0; i < words; i++)
        {
            ulong e = Expected(pattern, (ulong)p, i, seed);
            ulong a = p[i];
            if (a != e)
            {
                // Re-read once to separate read glitches from stored corruption (both are errors).
                errors++;
                if (errors <= 50) onError?.Invoke(i, e, a);
            }
        }
        // Inverted pass for patterns that benefit from bit flips both ways.
        if (pattern is Pattern.Checkerboard or Pattern.WalkingOnes)
        {
            for (long i = 0; i < words; i++) p[i] = ~Expected(pattern, (ulong)p, i, seed);
            for (long i = 0; i < words; i++)
            {
                ulong e = ~Expected(pattern, (ulong)p, i, seed);
                if (p[i] != e) { errors++; if (errors <= 50) onError?.Invoke(i, e, p[i]); }
            }
        }
        return errors;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Expected(Pattern pattern, ulong baseAddr, long i, ulong seed) => pattern switch
    {
        Pattern.AddressInAddress => (baseAddr + (ulong)i * 8) ^ seed,
        Pattern.Checkerboard => (i & 1) == 0 ? 0xAAAAAAAAAAAAAAAAUL : 0x5555555555555555UL,
        Pattern.WalkingOnes => 1UL << (int)((ulong)i % 64),
        _ => Hash((ulong)i + seed * 0x9E3779B97F4A7C15UL),
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Hash(ulong x)
    {
        x ^= x >> 33; x *= 0xFF51AFD7ED558CCDUL; x ^= x >> 33; x *= 0xC4CEB9FE1A85EC53UL; x ^= x >> 33;
        return x;
    }
}
