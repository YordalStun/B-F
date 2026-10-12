using System.Diagnostics;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using BenchForge.Core.Engine;
using BenchForge.Core.Model;
using BenchForge.Core.Scoring;

namespace BenchForge.Core.Storage;

/// <summary>
/// Drive benchmark using unbuffered, write-through I/O (bypasses the Windows cache, like CrystalDiskMark),
/// plus a data-integrity check (every 1 MB block is hashed on write and verified on read).
/// The stress variant loops write/verify for the whole step.
/// </summary>
public sealed unsafe class StorageModule : ITestModule
{
    private const FileOptions NoBuffering = (FileOptions)0x20000000;
    private const int SeqBlock = 1 << 20;
    private const int RandBlock = 4096;

    public Component Component => Component.Storage;

    public static string DefaultTarget() =>
        OperatingSystem.IsWindows() ? Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\" : Path.GetTempPath();

    public Task<ModuleResult> RunAsync(StepContext ctx) => Task.FromResult(Run(ctx));

    private ModuleResult Run(StepContext ctx)
    {
        var result = new ModuleResult { Component = Component.Storage, Kind = ctx.Kind };
        var target = !string.IsNullOrWhiteSpace(ctx.Step.Target) ? ctx.Step.Target
                   : string.IsNullOrWhiteSpace(ctx.Settings.StorageTarget) ? DefaultTarget() : ctx.Settings.StorageTarget;
        if (ctx.Step.Variant == StepVariants.DriveHealth)
        {
            result.Info["Target"] = target;
            DriveHealth.Run(ctx, target, result);
            ctx.Progress("Done", 1);
            return result;
        }
        string dir = Path.Combine(target, "BenchForgeTemp");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"bf-{Guid.NewGuid():N}.tmp");
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(target)) ?? target);
        long free = 0;
        try { free = drive.AvailableFreeSpace; } catch { }
        long fileSize = Math.Min((long)ctx.Settings.StorageFileMB << 20, Math.Max(64L << 20, free / 4));
        fileSize = fileSize / SeqBlock * SeqBlock;
        result.Info["Target"] = target;
        result.Info["Test file"] = $"{fileSize >> 20} MB";
        try { result.Info["Drive"] = $"{drive.Name} {drive.DriveFormat}, {free / 1e9:0} GB free of {drive.TotalSize / 1e9:0} GB"; } catch { }

        bool unbuffered = true;
        SafeFileHandle handle;
        try
        {
            handle = File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, FileOptions.WriteThrough | NoBuffering | FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        }
        catch (Exception)
        {
            unbuffered = false; // e.g. file systems that don't support direct I/O
            handle = File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, FileOptions.WriteThrough | FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        }
        result.Info["Cache bypass"] = unbuffered ? "Yes (unbuffered I/O)" : "No (file system does not support it — results include OS cache)";

        using (handle)
        {
            try
            {
                if (ctx.Kind == StepKind.Benchmark) Benchmark(ctx, handle, fileSize, result, dir);
                else Stress(ctx, handle, fileSize, result);
            }
            finally
            {
                handle.Dispose();
                try { if (File.Exists(path)) File.Delete(path); } catch { }
                try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
            }
        }
        ctx.Progress("Done", 1);
        return result;
    }

    private void Benchmark(StepContext ctx, SafeFileHandle h, long fileSize, ModuleResult result, string dir)
    {
        // Phases (time-boxed so the total step length — and the ETA — stay exact).
        var phases = new (string Name, double Weight)[]
        {
            ("Sequential write 1 MB Q8", 1.2), ("Sequential read 1 MB Q8 + verify", 1), ("4K random read Q1", 1), ("4K random read Q32", 1),
            ("4K random read Q32 × 4 threads", 0.8), ("4K random write Q1", 1), ("4K random write Q32", 1), ("Mixed 70/30 4K Q8", 1), ("Small files", 1),
        };
        double unit = Math.Max(0.8, (ctx.DurationSeconds - 3) / phases.Sum(p => p.Weight));
        int pi = 0;
        double Phase(out string name) { name = phases[pi].Name; ctx.Progress(name, pi / (double)phases.Length); return phases[pi++].Weight * unit; }
        void Live(double mbs) => ctx.Metric("disk.speed", "Drive test speed", Math.Round(mbs, 1), "MB/s");
        QueuedResult Q(QueuedTest t, long area) => QueuedIo.RunAsync(h, area, t, ctx.Token, Live).GetAwaiter().GetResult();

        var hashes = new ulong[fileSize / SeqBlock];
        var seqW = Q(new QueuedTest { BlockSize = SeqBlock, QueueDepth = 8, Sequential = true, ReadFraction = 0, Seconds = Phase(out _), SinglePass = true, Hashes = hashes }, fileSize);
        long usable = Math.Max(SeqBlock, seqW.Bytes / SeqBlock * SeqBlock);
        result.Info["Area tested"] = $"{usable >> 20} MB";
        var seqR = Q(new QueuedTest { BlockSize = SeqBlock, QueueDepth = 8, Sequential = true, Seconds = Phase(out _), SinglePass = true, Hashes = hashes }, usable);
        var r4q1 = Q(new QueuedTest { Seconds = Phase(out _), Latency = true }, usable);
        var r4q32 = Q(new QueuedTest { QueueDepth = 32, Seconds = Phase(out _) }, usable);
        var r4q32t4 = Q(new QueuedTest { QueueDepth = 32, Threads = 4, Seconds = Phase(out _) }, usable);
        var w4q1 = Q(new QueuedTest { ReadFraction = 0, Seconds = Phase(out _), Latency = true }, usable);
        var w4q32 = Q(new QueuedTest { ReadFraction = 0, QueueDepth = 32, Seconds = Phase(out _) }, usable);
        var mixed = Q(new QueuedTest { ReadFraction = 0.7, QueueDepth = 8, Seconds = Phase(out _) }, usable);
        double files = SmallFiles(ctx, dir, Phase(out _));

        void Add(string group, string name, double v, double reference, string unitText = "MB/s", string? detail = null) =>
            result.SubScores.Add(new SubScore { Name = name, Group = group, Value = Math.Round(v, 1), Unit = unitText, Score = Math.Round(Score.Linear(v, reference)), Detail = detail });
        string Iops(double mbs, int block = RandBlock) => $"{mbs * 1e6 / block:N0} IOPS";
        Add("Sequential", "Sequential read (1 MB, Q8)", seqR.MBs, ScoreReferences.DiskSeqRead);
        Add("Sequential", "Sequential write (1 MB, Q8)", seqW.MBs, ScoreReferences.DiskSeqWrite);
        Add("Random 4K", "4K random read (Q1)", r4q1.MBs, ScoreReferences.Disk4kReadQ1, detail: $"{Iops(r4q1.MBs)} · latency {r4q1.Latency}");
        Add("Random 4K", "4K random write (Q1)", w4q1.MBs, ScoreReferences.Disk4kWriteQ1, detail: $"{Iops(w4q1.MBs)} · latency {w4q1.Latency}");
        Add("Random 4K", "4K random read (Q32)", r4q32.MBs, ScoreReferences.Disk4kReadMT, detail: Iops(r4q32.MBs));
        Add("Random 4K", "4K random write (Q32)", w4q32.MBs, ScoreReferences.Disk4kWriteMT, detail: Iops(w4q32.MBs));
        Add("Random 4K", "4K random read (Q32 × 4 threads)", r4q32t4.MBs, ScoreReferences.Disk4kReadQ32T4, detail: Iops(r4q32t4.MBs));
        Add("Real world", "Mixed 70% read / 30% write (4K, Q8)", mixed.MBs, ScoreReferences.DiskMixed, detail: Iops(mixed.MBs));
        if (files > 0) Add("Real world", "Small files (create / read / delete)", files, ScoreReferences.DiskSmallFiles, "files/s");

        if (r4q1.Latency is LatencyStats rl)
        {
            result.Info["4K read latency"] = rl.ToString();
            result.SubScores.Add(new SubScore { Name = "Read stalls over 50 ms", Group = "Latency", Value = rl.Stalls, HigherIsBetter = false, Detail = $"99.9% of reads finished within {LatencyStats.Format(rl.P999Us)}" });
        }
        if (w4q1.Latency is LatencyStats wl)
        {
            result.Info["4K write latency"] = wl.ToString();
            result.SubScores.Add(new SubScore { Name = "Write stalls over 50 ms", Group = "Latency", Value = wl.Stalls, HigherIsBetter = false, Detail = $"99.9% of writes finished within {LatencyStats.Format(wl.P999Us)}" });
        }

        int mismatches = seqR.Mismatches;
        result.ErrorCount += mismatches;
        if (mismatches > 0) ctx.Error(new TestError { Test = "Storage verify", Message = $"{mismatches} corrupted 1 MB blocks read back" });
        result.Info["Data integrity"] = mismatches == 0 ? $"OK — {seqR.Bytes >> 20} MB verified" : $"{mismatches} corrupted 1 MB blocks!";
        if (!ctx.Token.IsCancellationRequested)
            result.Score = Math.Round(Score.GeoMean(result.SubScores.Where(s => s.Score > 0).Select(s => s.Score)));
    }

    /// <summary>Creates, writes, reads back and deletes many small files (4–64 KB) — what copying a folder of documents feels like.</summary>
    private static double SmallFiles(StepContext ctx, string dir, double seconds)
    {
        var folder = Path.Combine(dir, "small-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var rng = new Random(5);
        var data = new byte[64 * 1024];
        rng.NextBytes(data);
        long count = 0;
        var sw = Stopwatch.StartNew();
        try
        {
            while (sw.Elapsed.TotalSeconds < seconds && !ctx.Token.IsCancellationRequested)
            {
                var batch = new List<string>(100);
                for (int i = 0; i < 100; i++)
                {
                    var f = Path.Combine(folder, $"f{count + i}.bin");
                    int size = 4096 * rng.Next(1, 17);
                    using (var fs = new FileStream(f, FileMode.Create, FileAccess.Write, FileShare.None, 4096))
                    {
                        fs.Write(data, 0, size);
                        fs.Flush(true);
                    }
                    batch.Add(f);
                }
                foreach (var f in batch) { if (File.ReadAllBytes(f).Length == 0) throw new IOException("Empty read"); }
                foreach (var f in batch) File.Delete(f);
                count += batch.Count;
            }
        }
        catch (Exception ex) { ctx.Log("Small-files test stopped: " + ex.Message, Severity.Warning); }
        finally { try { Directory.Delete(folder, true); } catch { } }
        return count / Math.Max(0.001, sw.Elapsed.TotalSeconds);
    }

    private void Stress(StepContext ctx, SafeFileHandle h, long fileSize, ModuleResult result)
    {
        var hashes = new ulong[fileSize / SeqBlock];
        int loops = 0, mismatches = 0;
        long bytes = 0;
        var speeds = new List<double>();
        while (ctx.Remaining.TotalSeconds > 2 && !ctx.Token.IsCancellationRequested)
        {
            double slice = Math.Min(30, ctx.Remaining.TotalSeconds / 2);
            ctx.Progress($"Write/verify loop {loops + 1}", ctx.Elapsed.TotalSeconds / ctx.DurationSeconds);
            var (w, written) = Sequential(ctx, h, fileSize, slice, true, hashes, false, out _, seed: (ulong)loops + 1);
            var (r, _) = Sequential(ctx, h, Math.Max(SeqBlock, written / SeqBlock * SeqBlock), slice, false, hashes, true, out int mm, seed: (ulong)loops + 1);
            mismatches += mm; bytes += written * 2; loops++;
            speeds.Add(w); speeds.Add(r);
            if (mm > 0) ctx.Error(new TestError { Test = "Storage verify", Message = $"{mm} corrupted blocks read back in loop {loops}" });
        }
        result.ErrorCount = mismatches;
        result.Info["Loops"] = loops.ToString();
        result.Info["Data written + verified"] = $"{bytes / 1e9:0.0} GB";
        if (speeds.Count > 0) result.Info["Speed range"] = $"{speeds.Min():0} – {speeds.Max():0} MB/s";
        result.SubScores.Add(new SubScore { Name = "Corrupted blocks", Group = "Stress", Value = mismatches, HigherIsBetter = false });
    }

    private static void FillBlock(byte* p, int len, ulong seed)
    {
        ulong s = seed * 0x9E3779B97F4A7C15UL | 1;
        var span = new Span<ulong>(p, len / 8);
        for (int i = 0; i < span.Length; i++) { s ^= s << 13; s ^= s >> 7; s ^= s << 17; span[i] = s; }
    }

    private static (double MBs, long Bytes) Sequential(StepContext ctx, SafeFileHandle h, long size, double seconds, bool write, ulong[] hashes, bool verify, out int mismatches, ulong seed = 1)
    {
        int threads = 4;
        long blocks = size / SeqBlock;
        long next = -1, done = 0;
        int bad = 0;
        var stopAt = Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency);
        long start = Stopwatch.GetTimestamp();
        var workers = Enumerable.Range(0, threads).Select(_ => new Thread(() =>
        {
            byte* buf = (byte*)NativeMemory.AlignedAlloc(SeqBlock, 4096);
            try
            {
                var span = new Span<byte>(buf, SeqBlock);
                while (Stopwatch.GetTimestamp() < stopAt && !ctx.Token.IsCancellationRequested)
                {
                    long b = Interlocked.Increment(ref next);
                    if (b >= blocks) break;
                    if (write)
                    {
                        FillBlock(buf, SeqBlock, seed * 1_000_003 + (ulong)b);
                        hashes[b] = XxHash3.HashToUInt64(span);
                        RandomAccess.Write(h, span, b * SeqBlock);
                    }
                    else
                    {
                        int n = RandomAccess.Read(h, span, b * SeqBlock);
                        if (verify && (n != SeqBlock || XxHash3.HashToUInt64(span) != hashes[b])) Interlocked.Increment(ref bad);
                    }
                    Interlocked.Add(ref done, SeqBlock);
                }
            }
            finally { NativeMemory.AlignedFree(buf); }
        }) { IsBackground = true }).ToList();
        workers.ForEach(w => w.Start());
        ReportWhileRunning(ctx, workers, () => Interlocked.Read(ref done), write ? "Drive sequential write" : "Drive sequential read");
        double elapsed = (Stopwatch.GetTimestamp() - start) / (double)Stopwatch.Frequency;
        mismatches = bad;
        // If the whole file was processed quickly, time is shorter than the slot: that's fine, rate is still bytes/time.
        return (done / 1e6 / Math.Max(1e-6, elapsed), done);
    }

    private static void ReportWhileRunning(StepContext ctx, List<Thread> workers, Func<long> bytes, string name)
    {
        long last = 0; var t = Stopwatch.StartNew(); double lastT = 0;
        while (workers.Any(w => w.IsAlive))
        {
            Thread.Sleep(200);
            double now = t.Elapsed.TotalSeconds;
            if (now - lastT >= 0.9)
            {
                long b = bytes();
                ctx.Metric("disk.speed", "Drive test speed", Math.Round((b - last) / 1e6 / (now - lastT), 1), "MB/s");
                last = b; lastT = now;
            }
        }
    }
}
