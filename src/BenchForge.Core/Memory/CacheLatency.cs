using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using BenchForge.Core.Engine;
using BenchForge.Core.Model;
using BenchForge.Core.Platform;

namespace BenchForge.Core.Memory;

/// <summary>A cache level found from the latency ladder.</summary>
public sealed record CacheLevel(string Name, long SizeBytes, double LatencyNs);

/// <summary>Pure analysis of the latency-vs-size curve (unit tested).</summary>
public static class CacheAnalysis
{
    /// <summary>
    /// Finds cache levels from (buffer size, ns per load) points sorted by size: a level ends where latency jumps by ≥ 45 %
    /// over the plateau before it. The last plateau is RAM.
    /// </summary>
    public static List<CacheLevel> DetectLevels(IReadOnlyList<(long Size, double Ns)> pts)
    {
        var levels = new List<CacheLevel>();
        if (pts.Count < 3) return levels;
        int plateauStart = 0;
        for (int i = 1; i < pts.Count; i++)
        {
            double plateau = Median(pts.Skip(plateauStart).Take(i - plateauStart).Select(p => p.Ns));
            if (pts[i].Ns >= plateau * 1.45)
            {
                // The level's capacity is the largest size that still ran at plateau speed.
                int last = i - 1;
                while (last > plateauStart && pts[last].Ns > plateau * 1.15) last--;
                levels.Add(new CacheLevel($"L{levels.Count + 1}", pts[last].Size, Math.Round(plateau, 2)));
                // Desktop CPUs have three cache levels; later steps are RAM page-table (TLB) effects, not another cache.
                if (levels.Count == 3) { plateauStart = i; break; }
                // Skip over the transition before starting the next plateau.
                plateauStart = i;
                while (plateauStart + 1 < pts.Count && pts[plateauStart + 1].Ns >= pts[plateauStart].Ns * 1.25) plateauStart++;
                i = plateauStart;
            }
        }
        // RAM latency as measured at the largest sizes (includes page-table misses, like real programs see).
        double ram = Median(pts.Skip(Math.Max(plateauStart, pts.Count - 3)).Select(p => p.Ns));
        levels.Add(new CacheLevel("RAM", pts[^1].Size, Math.Round(ram, 1)));
        return levels;
    }

    private static double Median(IEnumerable<double> v)
    {
        var s = v.OrderBy(x => x).ToList();
        return s.Count == 0 ? 0 : s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
    }

    /// <summary>Sizes for the ladder: 4 KB … max, ×√2 steps.</summary>
    public static List<long> LadderSizes(long max)
    {
        var list = new List<long>();
        for (double s = 4096; s <= max; s *= Math.Sqrt(2)) list.Add(((long)s + 63) & ~63L);
        return list.Distinct().ToList();
    }

    public static string FormatSize(long b) => b >= 1 << 30 ? $"{b / (double)(1 << 30):0.#} GB" : b >= 1 << 20 ? $"{b / (double)(1 << 20):0.#} MB" : $"{b / 1024.0:0} KB";
}

/// <summary>
/// CPU cache and core-to-core latency: a latency and bandwidth ladder from 4 KB to 1 GB (shows L1/L2/L3/RAM),
/// and a ping-pong between every pair of cores (shows slow paths between core clusters / chiplets).
/// </summary>
public static unsafe class CacheLatency
{
    public static ModuleResult Run(StepContext ctx)
    {
        var result = new ModuleResult { Component = Component.Memory, Kind = StepKind.Benchmark };
        var (_, avail) = MemoryStatus.Get();
        long max = Math.Min(1L << 30, Math.Max(64L << 20, (long)avail / 4));
        var sizes = CacheAnalysis.LadderSizes(max);
        double budget = Math.Max(10, ctx.DurationSeconds - 2);
        int cores = Math.Min(Environment.ProcessorCount, 64);
        // Time split: 45 % latency ladder, 25 % bandwidth ladder, 30 % core-to-core.
        double perLat = budget * 0.45 / sizes.Count, perBw = budget * 0.25 / sizes.Count;
        result.Info["Sizes tested"] = $"{CacheAnalysis.FormatSize(sizes[0])} … {CacheAnalysis.FormatSize(sizes[^1])}";

        var lat = new List<(long, double)>();
        var bw = new List<(long, double)>();
        byte* buf = (byte*)NativeMemory.AlignedAlloc((nuint)max, 4096);
        try
        {
            var t = new Thread(() =>
            {
                ThreadPinning.Pin(0);
                for (int i = 0; i < sizes.Count && !ctx.Token.IsCancellationRequested; i++)
                {
                    ctx.Progress($"Latency at {CacheAnalysis.FormatSize(sizes[i])}", 0.45 * i / sizes.Count);
                    lat.Add((sizes[i], Chase(buf, sizes[i], perLat)));
                }
                for (int i = 0; i < sizes.Count && !ctx.Token.IsCancellationRequested; i++)
                {
                    ctx.Progress($"Bandwidth at {CacheAnalysis.FormatSize(sizes[i])}", 0.45 + 0.25 * i / sizes.Count);
                    bw.Add((sizes[i], ReadBandwidth(buf, sizes[i], perBw)));
                }
            }) { IsBackground = true, Name = "Cache ladder" };
            t.Start(); t.Join();
        }
        finally { NativeMemory.AlignedFree(buf); }

        result.Charts.Add(new ChartData
        {
            Title = "Memory latency by block size (shows each cache level)", XLabel = "Block size (KB)", YLabel = "ns", Unit = "ns",
            Points = lat.Select(p => new[] { p.Item1 / 1024.0, Math.Round(p.Item2, 2) }).ToList(),
        });
        result.Charts.Add(new ChartData
        {
            Title = "Single-core read bandwidth by block size", XLabel = "Block size (KB)", YLabel = "GB/s", Unit = "GB/s",
            Points = bw.Select(p => new[] { p.Item1 / 1024.0, Math.Round(p.Item2, 1) }).ToList(),
        });
        var levels = CacheAnalysis.DetectLevels(lat);
        foreach (var l in levels)
        {
            var bwAt = bw.Where(b => b.Item1 <= Math.Max(sizes[0], l.SizeBytes / 2)).Select(b => b.Item2).DefaultIfEmpty(0).Last();
            if (l.Name == "RAM") bwAt = bw.Count > 0 ? bw[^1].Item2 : 0;
            result.SubScores.Add(new SubScore { Name = $"{l.Name} latency", Group = "Cache", Value = l.LatencyNs, Unit = "ns", HigherIsBetter = false });
            result.SubScores.Add(new SubScore { Name = $"{l.Name} read speed (1 core)", Group = "Cache", Value = Math.Round(bwAt, 1), Unit = "GB/s" });
            if (l.Name != "RAM") result.Info[$"{l.Name} size (measured)"] = $"≈ {CacheAnalysis.FormatSize(l.SizeBytes)} per core";
        }

        // Core-to-core.
        if (cores > 1 && !ctx.Token.IsCancellationRequested)
        {
            var matrix = CoreToCore(ctx, cores, budget * 0.3);
            var vals = new List<double>();
            for (int i = 0; i < cores; i++) for (int j = i + 1; j < cores; j++) if (matrix[i, j] > 0) vals.Add(matrix[i, j]);
            if (vals.Count > 0)
            {
                result.SubScores.Add(new SubScore { Name = "Core-to-core latency (best)", Group = "Core to core", Value = Math.Round(vals.Min(), 1), Unit = "ns", HigherIsBetter = false });
                result.SubScores.Add(new SubScore { Name = "Core-to-core latency (average)", Group = "Core to core", Value = Math.Round(vals.Average(), 1), Unit = "ns", HigherIsBetter = false });
                result.SubScores.Add(new SubScore { Name = "Core-to-core latency (worst)", Group = "Core to core", Value = Math.Round(vals.Max(), 1), Unit = "ns", HigherIsBetter = false });
                result.Charts.Add(new ChartData
                {
                    Title = "Latency from core 0 to every other core", XLabel = "Core (#)", YLabel = "ns", Unit = "ns",
                    Points = Enumerable.Range(1, cores - 1).Where(j => matrix[0, j] > 0).Select(j => new[] { (double)j, Math.Round(matrix[0, j], 1) }).ToList(),
                });
                result.Info["Core-to-core matrix (ns)"] = FormatMatrix(matrix, cores);
            }
        }
        ctx.Progress("Done", 1);
        return result;
    }

    /// <summary>Average ns per dependent load in a random cycle over <paramref name="size"/> bytes.</summary>
    private static double Chase(byte* buf, long size, double seconds)
    {
        long nodes = size / 64;
        long* idx = (long*)buf;
        var perm = new int[nodes];
        for (int i = 0; i < nodes; i++) perm[i] = i;
        var rng = new Random(7);
        for (int i = (int)nodes - 1; i > 0; i--) { int j = rng.Next(i); (perm[i], perm[j]) = (perm[j], perm[i]); }
        for (int i = 0; i < nodes; i++) idx[(long)perm[i] * 8] = (long)perm[(i + 1) % nodes] * 8;
        long cur = 0;
        for (long i = 0; i < Math.Min(nodes * 2, 2_000_000); i++) cur = idx[cur]; // warm the caches/TLB
        long steps = 0;
        var sw = Stopwatch.StartNew();
        double limit = Math.Max(0.05, seconds);
        while (sw.Elapsed.TotalSeconds < limit)
        {
            for (int i = 0; i < 50_000; i++) cur = idx[cur];
            steps += 50_000;
        }
        return sw.Elapsed.TotalMilliseconds * 1e6 / Math.Max(1, steps) + (cur & 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static double ReadBandwidth(byte* buf, long size, double seconds)
    {
        ulong sink = 0; long bytes = 0;
        int w = Vector<byte>.Count;
        var sw = Stopwatch.StartNew();
        double limit = Math.Max(0.05, seconds);
        while (sw.Elapsed.TotalSeconds < limit)
        {
            var v0 = Vector<ulong>.Zero; var v1 = Vector<ulong>.Zero; var v2 = Vector<ulong>.Zero; var v3 = Vector<ulong>.Zero;
            byte* p = buf; byte* end = buf + size - 4 * w;
            for (; p <= end; p += 4 * w)
            {
                v0 += Unsafe.ReadUnaligned<Vector<ulong>>(p); v1 += Unsafe.ReadUnaligned<Vector<ulong>>(p + w);
                v2 += Unsafe.ReadUnaligned<Vector<ulong>>(p + 2 * w); v3 += Unsafe.ReadUnaligned<Vector<ulong>>(p + 3 * w);
            }
            sink += Vector.Sum(v0 + v1 + v2 + v3);
            bytes += size;
        }
        return bytes / 1e9 / sw.Elapsed.TotalSeconds + (sink & 0);
    }

    /// <summary>Round-trip ping-pong on one cache line between two pinned cores; returns one-way latency in ns.</summary>
    private static double[,] CoreToCore(StepContext ctx, int cores, double budget)
    {
        var m = new double[cores, cores];
        int pairs = cores * (cores - 1) / 2;
        double perPair = Math.Clamp(budget / pairs, 0.01, 0.3);
        long* line = (long*)NativeMemory.AlignedAlloc(128, 128);
        try
        {
            int done = 0;
            for (int i = 0; i < cores && !ctx.Token.IsCancellationRequested; i++)
                for (int j = i + 1; j < cores && !ctx.Token.IsCancellationRequested; j++)
                {
                    ctx.Progress($"Core-to-core latency {i} ↔ {j}", 0.7 + 0.3 * done++ / pairs);
                    m[i, j] = m[j, i] = PingPong(line, i, j, perPair);
                }
        }
        finally { NativeMemory.AlignedFree(line); }
        return m;
    }

    private static double PingPong(long* line, int a, int b, double seconds)
    {
        *line = 0;
        long rounds = 0;
        bool stop = false;
        double result = 0;
        long* l = line;
        var responder = new Thread(() =>
        {
            ThreadPinning.Pin(b);
            long expect = 1;
            while (!Volatile.Read(ref stop))
            {
                if (Volatile.Read(ref *l) == expect) { Volatile.Write(ref *l, expect + 1); expect += 2; }
            }
        }) { IsBackground = true };
        var initiator = new Thread(() =>
        {
            ThreadPinning.Pin(a);
            long v = 0;
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < seconds)
            {
                for (int k = 0; k < 1000; k++)
                {
                    Volatile.Write(ref *l, v + 1);
                    long spins = 0;
                    while (Volatile.Read(ref *l) != v + 2) { if (++spins > 50_000_000) { Volatile.Write(ref stop, true); return; } }
                    v += 2;
                }
                rounds += 1000;
            }
            result = sw.Elapsed.TotalMilliseconds * 1e6 / Math.Max(1, rounds) / 2;
            Volatile.Write(ref stop, true);
        }) { IsBackground = true };
        responder.Start(); initiator.Start();
        initiator.Join(); responder.Join();
        return result;
    }

    private static string FormatMatrix(double[,] m, int n)
    {
        if (n > 32) return "(too many cores to list — see the chart)";
        var sb = new StringBuilder();
        sb.Append("     ").AppendJoin("", Enumerable.Range(0, n).Select(j => $"{j,5}")).AppendLine();
        for (int i = 0; i < n; i++)
        {
            sb.Append($"{i,4} ");
            for (int j = 0; j < n; j++) sb.Append(i == j ? "    -" : $"{m[i, j],5:0}");
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }
}
