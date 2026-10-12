using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using BenchForge.Core.Engine;
using BenchForge.Core.Model;

namespace BenchForge.Core.Storage;

/// <summary>Result of looking for the point where a drive's fast write cache (SLC) runs out.</summary>
public sealed record SlcResult(double? CacheGB, double CachedMBs, double AfterMBs, bool Detected);

public static class DriveAnalysis
{
    /// <summary>
    /// Finds the knee in a sustained-write curve (x = GB written, y = MB/s): the first point after which speed stays
    /// below 60 % of the early speed.
    /// </summary>
    public static SlcResult DetectSlc(IReadOnlyList<double[]> pts)
    {
        if (pts.Count < 8) return new SlcResult(null, pts.Count > 0 ? pts.Average(p => p[1]) : 0, 0, false);
        var ys = pts.Select(p => p[1]).ToList();
        double early = Median(ys.Take(Math.Max(3, ys.Count / 10)).ToList());
        for (int i = 2; i < ys.Count - 3; i++)
        {
            var rest = ys.Skip(i).ToList();
            double window = Median(ys.Skip(i).Take(5).ToList());
            if (window < early * 0.6 && rest.Count(v => v < early * 0.7) >= rest.Count * 0.8)
                return new SlcResult(pts[i][0], early, Median(rest), true);
        }
        return new SlcResult(null, early, Median(ys.Skip(ys.Count / 2).ToList()), false);
    }

    public static double Median(List<double> v)
    {
        if (v.Count == 0) return 0;
        var s = v.OrderBy(x => x).ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
    }

    /// <summary>Visits block indices 0..count-1 in bit-reversed order so any prefix covers the disk evenly.</summary>
    public static IEnumerable<long> SpreadOrder(long count)
    {
        int bits = 1;
        while ((1L << bits) < count) bits++;
        for (long i = 0; i < (1L << bits); i++)
        {
            long r = 0, x = i;
            for (int b = 0; b < bits; b++) { r = (r << 1) | (x & 1); x >>= 1; }
            if (r < count) yield return r;
        }
    }
}

/// <summary>Drive health check: read-only surface scan of the whole disk + sustained-write (SLC cache) test.</summary>
internal static partial class DriveHealth
{
    private const int ScanBlock = 4 << 20;
    private const int Zones = 200;
    private const FileOptions NoBuffering = (FileOptions)0x20000000;

    public static void Run(StepContext ctx, string target, ModuleResult result)
    {
        double scanSeconds = (ctx.DurationSeconds - 4) * 0.55;
        SurfaceScan(ctx, target, scanSeconds, result);
        SustainedWrite(ctx, target, Math.Max(5, ctx.Remaining.TotalSeconds - 2), result);
    }

    // ---------------------------------------------------------------- surface scan

    private static void SurfaceScan(StepContext ctx, string target, double seconds, ModuleResult result)
    {
        ctx.Progress("Surface scan (read-only)", 0.02);
        SafeFileHandle? h = null;
        long size = 0;
        string what;
        string? tempFile = null;
        try
        {
            if (OperatingSystem.IsWindows() && PhysicalDiskFor(target) is int disk)
            {
                what = $"Physical disk {disk}";
                h = File.OpenHandle($@"\\.\PhysicalDrive{disk}", FileMode.Open, FileAccess.Read, FileShare.ReadWrite, NoBuffering);
                size = DiskLength(h);
            }
            else
            {
                // Non-Windows (or no disk access): scan a test file instead so the test still runs.
                tempFile = Path.Combine(target, "BenchForgeTemp", $"scan-{Guid.NewGuid():N}.tmp");
                Directory.CreateDirectory(Path.GetDirectoryName(tempFile)!);
                using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                {
                    var buf = new byte[1 << 20];
                    new Random(3).NextBytes(buf);
                    for (int i = 0; i < 512; i++) fs.Write(buf);
                }
                what = "Test file (raw disk access not available)";
                h = File.OpenHandle(tempFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                size = new FileInfo(tempFile).Length;
            }
        }
        catch (Exception ex)
        {
            result.Info["Surface scan"] = "Not possible: " + ex.Message + " (run as administrator)";
            h?.Dispose();
            return;
        }

        using (h)
        {
            long blocks = size / ScanBlock;
            var zoneBytes = new double[Zones]; var zoneTime = new double[Zones];
            var zoneMaxMs = new double[Zones]; var zoneErrors = new int[Zones];
            int slow = 0, bad = 0; long readBlocks = 0;
            var badOffsets = new List<long>();
            using var buf = new NativeBuffer(ScanBlock);
            var deadline = Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency);
            foreach (var b in DriveAnalysis.SpreadOrder(blocks))
            {
                if (Stopwatch.GetTimestamp() > deadline || ctx.Token.IsCancellationRequested) break;
                int zone = (int)(b * Zones / Math.Max(1, blocks));
                long t0 = Stopwatch.GetTimestamp();
                try
                {
                    RandomAccess.Read(h, buf.GetSpan(), b * ScanBlock);
                    double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                    zoneBytes[zone] += ScanBlock; zoneTime[zone] += ms / 1000; zoneMaxMs[zone] = Math.Max(zoneMaxMs[zone], ms);
                    if (ms > 500) slow++;
                }
                catch (IOException ex)
                {
                    bad++; zoneErrors[zone]++;
                    if (badOffsets.Count < 50) badOffsets.Add(b * ScanBlock);
                    ctx.Error(new TestError { Test = "Surface scan", Message = $"Unreadable area at {b * ScanBlock / 1e9:0.000} GB", Detail = ex.Message });
                }
                readBlocks++;
                if (readBlocks % 16 == 0)
                    ctx.Progress($"Surface scan — {100.0 * readBlocks / Math.Max(1, blocks):0.0}% of {what.ToLowerInvariant()} read", 0.02 + 0.5 * Math.Min(1, (seconds - (deadline - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency) / seconds));
            }
            double coverage = blocks > 0 ? 100.0 * readBlocks / blocks : 0;
            var speeds = Enumerable.Range(0, Zones).Where(z => zoneTime[z] > 0).Select(z => (Zone: z, MBs: zoneBytes[z] / 1e6 / zoneTime[z])).ToList();
            result.Info["Surface scan"] = $"{what}, {size / 1e9:0} GB · {coverage:0.0}% read" + (coverage < 99.9 ? " (evenly sampled across the whole disk)" : " (complete)");
            result.SubScores.Add(new SubScore { Name = "Surface covered", Group = "Health check", Value = Math.Round(coverage, 1), Unit = "%" });
            result.SubScores.Add(new SubScore { Name = "Unreadable areas (bad sectors)", Group = "Health check", Value = bad, HigherIsBetter = false });
            result.SubScores.Add(new SubScore { Name = "Slow areas (> 500 ms per read)", Group = "Health check", Value = slow, HigherIsBetter = false });
            if (speeds.Count > 0)
            {
                result.SubScores.Add(new SubScore { Name = "Read speed across the disk (min / avg / max)", Group = "Health check", Value = Math.Round(speeds.Average(s => s.MBs)), Unit = "MB/s", Detail = $"{speeds.Min(s => s.MBs):0} / {speeds.Average(s => s.MBs):0} / {speeds.Max(s => s.MBs):0} MB/s" });
                result.Charts.Add(new ChartData
                {
                    Title = "Surface scan — read speed across the disk", XLabel = "Position on disk (%)", YLabel = "MB/s", Unit = "MB/s",
                    Points = speeds.Select(s => new[] { Math.Round(s.Zone * 100.0 / Zones, 1), Math.Round(s.MBs, 1) }).ToList(),
                    Note = bad > 0 ? $"{bad} unreadable areas, first at {badOffsets[0] / 1e9:0.00} GB" : slow > 0 ? $"{slow} slow reads over 500 ms" : "No unreadable or slow areas found",
                });
            }
            if (bad > 0) result.Info["Unreadable areas at (GB)"] = string.Join(", ", badOffsets.Take(20).Select(o => (o / 1e9).ToString("0.000")));
            result.ErrorCount += bad;
        }
        if (tempFile != null) try { File.Delete(tempFile); } catch { }
    }

    // ---------------------------------------------------------------- sustained write

    private static void SustainedWrite(StepContext ctx, string target, double seconds, ModuleResult result)
    {
        long free = 0;
        try { free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(target)) ?? target).AvailableFreeSpace; } catch { }
        long limit = Math.Min(free / 4, 64L << 30) / (1 << 20) * (1 << 20);
        if (limit < (2L << 30))
        {
            result.Info["Sustained write"] = "Skipped — less than 8 GB free on the drive";
            return;
        }
        var dir = Path.Combine(target, "BenchForgeTemp");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"sustained-{Guid.NewGuid():N}.tmp");
        var points = new List<double[]>();
        try
        {
            SafeFileHandle h;
            try { h = File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, FileOptions.WriteThrough | NoBuffering | FileOptions.Asynchronous | FileOptions.DeleteOnClose); }
            catch { h = File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, FileOptions.WriteThrough | FileOptions.Asynchronous | FileOptions.DeleteOnClose); }
            using (h)
            {
                ctx.Progress("Sustained write (fast-cache test)", 0.55);
                var t = new QueuedTest { BlockSize = 1 << 20, QueueDepth = 8, Sequential = true, SinglePass = true, ReadFraction = 0, Seconds = seconds };
                var startedAt = DateTime.Now;
                var res = QueuedIo.RunAsync(h, limit, t, ctx.Token,
                    mbs => ctx.Metric("disk.speed", "Drive test speed", Math.Round(mbs, 1), "MB/s"),
                    (mbs, total) =>
                    {
                        points.Add(new[] { Math.Round(total / 1e9, 2), Math.Round(mbs, 1) });
                        ctx.Progress($"Sustained write — {total / 1e9:0.0} GB written at {mbs:0} MB/s", 0.55 + 0.45 * Math.Min(1, (DateTime.Now - startedAt).TotalSeconds / seconds));
                    }).GetAwaiter().GetResult();
                var slc = DriveAnalysis.DetectSlc(points);
                result.SubScores.Add(new SubScore { Name = "Sustained write — total written", Group = "Health check", Value = Math.Round(res.Bytes / 1e9, 1), Unit = "GB", Detail = $"average {res.MBs:0} MB/s" });
                result.SubScores.Add(new SubScore { Name = "Write speed in fast cache", Group = "Health check", Value = Math.Round(slc.CachedMBs), Unit = "MB/s" });
                if (slc.Detected)
                {
                    result.SubScores.Add(new SubScore { Name = "Fast write cache (SLC) size", Group = "Health check", Value = Math.Round(slc.CacheGB ?? 0, 1), Unit = "GB" });
                    result.SubScores.Add(new SubScore { Name = "Write speed after cache is full", Group = "Health check", Value = Math.Round(slc.AfterMBs), Unit = "MB/s" });
                }
                result.Info["Sustained write"] = slc.Detected
                    ? $"Writes at {slc.CachedMBs:0} MB/s until about {slc.CacheGB:0.#} GB, then {slc.AfterMBs:0} MB/s ({slc.AfterMBs / Math.Max(1, slc.CachedMBs):P0} of cached speed)"
                    : $"Steady {slc.CachedMBs:0} MB/s for {res.Bytes / 1e9:0.#} GB — no cache drop within the test";
                result.Charts.Add(new ChartData
                {
                    Title = "Sustained write — speed vs amount written", XLabel = "GB written", YLabel = "MB/s", Unit = "MB/s", Points = points,
                    Note = result.Info["Sustained write"],
                });
            }
        }
        catch (Exception ex) { result.Info["Sustained write"] = "Failed: " + ex.Message; }
        finally { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    }

    // ---------------------------------------------------------------- Windows disk helpers

    [StructLayout(LayoutKind.Sequential)]
    private struct DISK_EXTENT { public uint DiskNumber; public long StartingOffset; public long ExtentLength; }

    [StructLayout(LayoutKind.Sequential)]
    private struct VOLUME_DISK_EXTENTS { public uint NumberOfDiskExtents; public DISK_EXTENT Extent0; }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle h, uint code, nint inBuf, uint inSize, out VOLUME_DISK_EXTENTS outBuf, uint outSize, out uint returned, nint overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle h, uint code, nint inBuf, uint inSize, out long outBuf, uint outSize, out uint returned, nint overlapped);

    private const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;
    private const uint IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C;

    public static int? PhysicalDiskFor(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var root = Path.GetPathRoot(Path.GetFullPath(path))?.TrimEnd('\\');
        if (string.IsNullOrEmpty(root) || root.Length != 2) return null;
        using var vol = File.OpenHandle($@"\\.\{root}", FileMode.Open, (FileAccess)0, FileShare.ReadWrite);
        if (!DeviceIoControl(vol, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, 0, 0, out VOLUME_DISK_EXTENTS ext, (uint)Marshal.SizeOf<VOLUME_DISK_EXTENTS>(), out _, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return (int)ext.Extent0.DiskNumber;
    }

    private static long DiskLength(SafeFileHandle disk)
    {
        if (!DeviceIoControl(disk, IOCTL_DISK_GET_LENGTH_INFO, 0, 0, out long len, 8, out _, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return len;
    }
}
