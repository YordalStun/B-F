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
        var target = string.IsNullOrWhiteSpace(ctx.Settings.StorageTarget) ? DefaultTarget() : ctx.Settings.StorageTarget;
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
            handle = File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, FileOptions.WriteThrough | NoBuffering | FileOptions.DeleteOnClose);
        }
        catch (Exception)
        {
            unbuffered = false; // e.g. file systems that don't support direct I/O
            handle = File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, FileOptions.WriteThrough | FileOptions.DeleteOnClose);
        }
        result.Info["Cache bypass"] = unbuffered ? "Yes (unbuffered I/O)" : "No (file system does not support it — results include OS cache)";

        using (handle)
        {
            try
            {
                if (ctx.Kind == StepKind.Benchmark) Benchmark(ctx, handle, fileSize, result);
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

    private void Benchmark(StepContext ctx, SafeFileHandle h, long fileSize, ModuleResult result)
    {
        double phase = Math.Max(1, (ctx.DurationSeconds - 2) / 6.0);
        int threads = Math.Clamp(Environment.ProcessorCount, 4, 8);
        var hashes = new ulong[fileSize / SeqBlock];

        ctx.Progress("Sequential write (1 MB blocks)", 0);
        var (seqW, written) = Sequential(ctx, h, fileSize, phase, write: true, hashes, verify: false, out _);
        long usable = Math.Max(SeqBlock, written / SeqBlock * SeqBlock);
        result.Info["Area tested"] = $"{usable >> 20} MB";
        ctx.Progress("Sequential read + verify", 1 / 6.0);
        var (seqR, _) = Sequential(ctx, h, usable, phase, write: false, hashes, verify: true, out int mismatches);
        ctx.Progress("Random 4K read, 1 thread", 2 / 6.0);
        double r4q1 = Random4K(ctx, h, usable, phase, 1, write: false);
        ctx.Progress($"Random 4K read, {threads} threads", 3 / 6.0);
        double r4mt = Random4K(ctx, h, usable, phase, threads, write: false);
        ctx.Progress("Random 4K write, 1 thread", 4 / 6.0);
        double w4q1 = Random4K(ctx, h, usable, phase, 1, write: true);
        ctx.Progress($"Random 4K write, {threads} threads", 5 / 6.0);
        double w4mt = Random4K(ctx, h, usable, phase, threads, write: true);

        void Add(string group, string name, double v, double reference, string? detail = null) =>
            result.SubScores.Add(new SubScore { Name = name, Group = group, Value = Math.Round(v, 1), Unit = "MB/s", Score = Math.Round(Score.Linear(v, reference)), Detail = detail });
        Add("Sequential", "Sequential read", seqR, ScoreReferences.DiskSeqRead);
        Add("Sequential", "Sequential write", seqW, ScoreReferences.DiskSeqWrite);
        Add("Random 4K", "4K random read (1 thread)", r4q1, ScoreReferences.Disk4kReadQ1, $"{r4q1 * 1e6 / RandBlock:0} IOPS");
        Add("Random 4K", "4K random write (1 thread)", w4q1, ScoreReferences.Disk4kWriteQ1, $"{w4q1 * 1e6 / RandBlock:0} IOPS");
        Add("Random 4K", $"4K random read ({threads} threads)", r4mt, ScoreReferences.Disk4kReadMT, $"{r4mt * 1e6 / RandBlock:0} IOPS");
        Add("Random 4K", $"4K random write ({threads} threads)", w4mt, ScoreReferences.Disk4kWriteMT, $"{w4mt * 1e6 / RandBlock:0} IOPS");

        result.ErrorCount += mismatches;
        result.Info["Data integrity"] = mismatches == 0 ? $"OK — {usable >> 20} MB verified" : $"{mismatches} corrupted 1 MB blocks!";
        if (!ctx.Token.IsCancellationRequested)
            result.Score = Math.Round(Score.GeoMean(result.SubScores.Select(s => s.Score)));
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

    private static double Random4K(StepContext ctx, SafeFileHandle h, long size, double seconds, int threads, bool write)
    {
        long blocks = size / RandBlock;
        long done = 0;
        var stopAt = Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency);
        long start = Stopwatch.GetTimestamp();
        var workers = Enumerable.Range(0, threads).Select(t => new Thread(() =>
        {
            byte* buf = (byte*)NativeMemory.AlignedAlloc(RandBlock, 4096);
            try
            {
                var span = new Span<byte>(buf, RandBlock);
                FillBlock(buf, RandBlock, (ulong)t + 99);
                var rng = new Random(1234 + t);
                while (Stopwatch.GetTimestamp() < stopAt && !ctx.Token.IsCancellationRequested)
                {
                    long off = rng.NextInt64(blocks) * RandBlock;
                    if (write) RandomAccess.Write(h, span, off); else RandomAccess.Read(h, span, off);
                    Interlocked.Add(ref done, RandBlock);
                }
            }
            finally { NativeMemory.AlignedFree(buf); }
        }) { IsBackground = true }).ToList();
        workers.ForEach(w => w.Start());
        ReportWhileRunning(ctx, workers, () => Interlocked.Read(ref done), write ? "Drive 4K write" : "Drive 4K read");
        double elapsed = (Stopwatch.GetTimestamp() - start) / (double)Stopwatch.Frequency;
        return done / 1e6 / Math.Max(1e-6, elapsed);
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
