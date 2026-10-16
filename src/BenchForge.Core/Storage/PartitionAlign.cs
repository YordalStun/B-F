using System.IO.Hashing;
using System.Text;
using BenchForge.Core.Persistence;

namespace BenchForge.Core.Storage;

/// <summary>One partition as Windows reports it, plus what we need to decide whether BenchForge may re-align it.</summary>
public sealed class PartitionEntry
{
    public int DiskNumber { get; set; }
    public int PartitionNumber { get; set; }
    public string DiskModel { get; set; } = "";
    public string MediaType { get; set; } = "";     // SSD / HDD / Unknown
    public string PartitionStyle { get; set; } = ""; // GPT / MBR
    public char? DriveLetter { get; set; }
    public long Offset { get; set; }
    public long Size { get; set; }
    public long? UsedBytes { get; set; }
    public string FileSystem { get; set; } = "";
    public string Label { get; set; } = "";
    public bool IsSystem { get; set; }
    public bool IsBoot { get; set; }
    public bool IsHidden { get; set; }
    public string? GptType { get; set; }
    public int? MbrType { get; set; }
    /// <summary>MBR logical partition inside an extended partition.</summary>
    public bool IsLogical { get; set; }
    public bool HasPageFile { get; set; }
    /// <summary>true = BitLocker / device encryption on, null = could not tell.</summary>
    public bool? Encrypted { get; set; }

    public bool IsAligned => Offset % PartitionAlign.PageSize == 0;
    public string Name => DriveLetter is char c ? $"{c}:" : $"Disk {DiskNumber} partition {PartitionNumber}";
}

/// <summary>A drive that can hold the temporary copy of the files.</summary>
public sealed record BackupVolume(char Letter, string FileSystem, long FreeBytes, int? DiskNumber, string Label)
{
    public string Text => $"{Letter}: {(string.IsNullOrEmpty(Label) ? "" : Label + " ")}— {FreeBytes / 1e9:0} GB free";
}

public sealed record AlignVerdict(bool CanAutoFix, string Reason);

public static class PartitionAlign
{
    public const long PageSize = 4096;
    public const long MiB = 1024 * 1024;
    /// <summary>Basic data partition (what Windows makes for D:, E: …).</summary>
    public const string GptBasicData = "{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}";
    /// <summary>MBR types of normal data partitions: FAT32 (0x0B/0x0C), FAT16 LBA (0x0E), NTFS/exFAT (0x07).</summary>
    private static readonly int[] MbrData = { 0x07, 0x0B, 0x0C, 0x0E };
    /// <summary>File systems Windows can format again (ReFS can't be formatted on Home/Pro).</summary>
    private static readonly string[] Supported = { "NTFS", "exFAT", "FAT32" };
    /// <summary>Extra room kept free on the backup drive.</summary>
    public const long BackupMargin = 2L * 1024 * MiB;

    /// <summary>
    /// The aligned replacement for a partition: start moved forward to the next 1 MiB boundary (what Windows itself uses),
    /// end unchanged or slightly earlier, so it always fits in the space the old partition used.
    /// </summary>
    public static (long Offset, long Size) NewLayout(long offset, long size)
    {
        long start = (offset + MiB - 1) / MiB * MiB;
        long end = offset + size;
        long newSize = (end - start) / MiB * MiB;
        return (start, Math.Max(0, newSize));
    }

    /// <summary>Whether BenchForge may fix this partition by itself (copy off → recreate aligned → copy back), and if not, why.</summary>
    public static AlignVerdict Check(PartitionEntry p, string? appDirectory = null, string? windowsDirectory = null)
    {
        if (p.IsAligned) return new(false, "Already aligned — nothing to do.");
        if (p.DriveLetter is not char letter) return new(false, "It has no drive letter (hidden system or recovery partition). Use a partition tool.");
        if (p.IsSystem || p.IsBoot || SameDrive(windowsDirectory, letter))
            return new(false, "This is the Windows partition. It can't be moved while Windows runs from it — follow the steps below.");
        if (p.HasPageFile) return new(false, "Windows keeps a page file on it. Move the page file (System › About › Advanced system settings › Performance › Virtual memory) and check again.");
        if (SameDrive(appDirectory, letter)) return new(false, "BenchForge itself runs from this drive. Copy BenchForge to another drive and run it from there.");
        if (p.Encrypted == true) return new(false, "It is encrypted with BitLocker. Turn BitLocker off for this drive first, or use a partition tool.");
        if (p.Encrypted == null) return new(false, "Could not check whether it is encrypted (BitLocker). Run BenchForge as administrator.");
        if (p.IsLogical) return new(false, "It is a 'logical' partition inside an extended partition (old MBR layout). Use a partition tool.");
        if (p.IsHidden) return new(false, "It is a hidden partition. Use a partition tool.");
        if (p.PartitionStyle == "GPT" && !string.Equals(p.GptType, GptBasicData, StringComparison.OrdinalIgnoreCase))
            return new(false, "It is a special partition (not a normal data partition). Use a partition tool.");
        if (p.PartitionStyle == "MBR" && p.MbrType is int t && !MbrData.Contains(t))
            return new(false, "It is a special partition type. Use a partition tool.");
        if (p.PartitionStyle is not ("GPT" or "MBR")) return new(false, "Unknown disk layout. Use a partition tool.");
        if (!Supported.Contains(p.FileSystem, StringComparer.OrdinalIgnoreCase)) return new(false, $"File system '{p.FileSystem}' is not supported here. Use a partition tool.");
        if (p.UsedBytes is not long used) return new(false, "Could not read how much space is used on it.");
        var (_, newSize) = NewLayout(p.Offset, p.Size);
        if (newSize < used + 256 * MiB) return new(false, "It is too full to recreate safely. Free some space first.");
        if (p.FileSystem.Equals("FAT32", StringComparison.OrdinalIgnoreCase) && newSize > 32L * 1024 * MiB)
            return new(false, "Windows can't format FAT32 partitions over 32 GB. Use a partition tool.");
        return new(true, "BenchForge can fix this one itself.");
    }

    private static bool SameDrive(string? path, char letter) =>
        !string.IsNullOrEmpty(path) && path.Length >= 2 && path[1] == ':' && char.ToUpperInvariant(path[0]) == char.ToUpperInvariant(letter);

    /// <summary>Drives that can hold all the files, best first (another physical disk, most free space). FAT32 is skipped (4 GB file limit).</summary>
    public static List<BackupVolume> BackupTargets(PartitionEntry p, IEnumerable<BackupVolume> volumes) =>
        volumes.Where(v => v.Letter != p.DriveLetter && !v.FileSystem.Equals("FAT32", StringComparison.OrdinalIgnoreCase)
                           && v.FreeBytes >= (p.UsedBytes ?? long.MaxValue / 2) + BackupMargin)
               .OrderBy(v => v.DiskNumber == p.DiskNumber ? 1 : 0).ThenByDescending(v => v.FreeBytes).ToList();

    /// <summary>PowerShell that deletes the partition and creates the aligned one in the same place, keeping letter, type, file system and label.</summary>
    public static string RecreateScript(PartitionEntry p)
    {
        var (offset, size) = NewLayout(p.Offset, p.Size);
        string Q(string s) => "'" + s.Replace("'", "''") + "'";
        // New-Partition only knows FAT12/FAT16/Huge/IFS/FAT32 (1/4/6/7/12): FAT32 LBA (0x0B/0x0C) → 12, FAT16 LBA (0x0E) → 6.
        int mbr = p.MbrType switch { 0x0B or 0x0C => 12, 0x0E => 6, _ => 7 };
        var type = p.PartitionStyle == "GPT" ? $"-GptType {Q(p.GptType ?? GptBasicData)}" : $"-MbrType {mbr}";
        var fs = p.FileSystem.Equals("FAT32", StringComparison.OrdinalIgnoreCase) ? "FAT32" : p.FileSystem.Equals("exFAT", StringComparison.OrdinalIgnoreCase) ? "exFAT" : "NTFS";
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine($"Remove-Partition -DiskNumber {p.DiskNumber} -PartitionNumber {p.PartitionNumber} -Confirm:$false");
        sb.AppendLine("Update-Disk -Number " + p.DiskNumber);
        sb.AppendLine($"$p = New-Partition -DiskNumber {p.DiskNumber} -Offset {offset} -Size {size} -DriveLetter {p.DriveLetter} {type}");
        sb.AppendLine($"Format-Volume -Partition $p -FileSystem {fs} -NewFileSystemLabel {Q(p.Label)} -Confirm:$false -Force | Out-Null");
        sb.AppendLine("Write-Output (\"NEWOFFSET=\" + $p.Offset)");
        return sb.ToString();
    }
}

/// <summary>
/// Size + fingerprint of every file in a folder tree, to prove a copy is identical. Links (symlinks, junctions) are recorded
/// by their target text with size -1 and never followed, so a copy that dropped or changed a link is caught too.
/// </summary>
public static class FileManifest
{
    /// <summary>Folders Windows recreates by itself; never copied or compared.</summary>
    public static readonly string[] Skip = { "System Volume Information", "$RECYCLE.BIN" };

    public static Dictionary<string, (long Size, ulong Hash)> Build(string root, Action<long>? bytesDone = null, CancellationToken ct = default)
    {
        var map = new Dictionary<string, (long, ulong)>(StringComparer.OrdinalIgnoreCase);
        var buf = new byte[1 << 20];
        long total = 0;
        foreach (var (file, link) in Walk(root))
        {
            ct.ThrowIfCancellationRequested();
            if (link is not null)
            {
                map[Path.GetRelativePath(root, file)] = (LinkSize, XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(link)));
                continue;
            }
            var h = new XxHash3();
            long len = 0;
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan))
            {
                int n;
                while ((n = fs.Read(buf, 0, buf.Length)) > 0) { h.Append(buf.AsSpan(0, n)); len += n; total += n; bytesDone?.Invoke(total); }
            }
            map[Path.GetRelativePath(root, file)] = (len, h.GetCurrentHashAsUInt64());
        }
        return map;
    }

    /// <summary>Size recorded for a link entry (its hash is of the target text).</summary>
    public const long LinkSize = -1;

    /// <summary>Every file, plus every link with its target (links are listed, never followed).</summary>
    private static IEnumerable<(string Path, string? LinkTarget)> Walk(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                var fi = new FileInfo(f);
                yield return (f, fi.Attributes.HasFlag(FileAttributes.ReparsePoint) ? fi.LinkTarget ?? "" : null);
            }
            foreach (var d in Directory.EnumerateDirectories(dir))
            {
                var info = new DirectoryInfo(d);
                if (dir == root && Skip.Contains(info.Name, StringComparer.OrdinalIgnoreCase)) continue;
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) { yield return (d, info.LinkTarget ?? ""); continue; }
                stack.Push(d);
            }
        }
    }

    /// <summary>Differences between two manifests (missing, extra or changed files). Empty = identical.</summary>
    public static List<string> Compare(IReadOnlyDictionary<string, (long Size, ulong Hash)> expected, IReadOnlyDictionary<string, (long Size, ulong Hash)> actual)
    {
        var diff = new List<string>();
        foreach (var (path, e) in expected)
        {
            if (!actual.TryGetValue(path, out var a)) diff.Add(e.Size == LinkSize ? $"missing link: {path}" : $"missing: {path}");
            else if ((a.Size == LinkSize) != (e.Size == LinkSize)) diff.Add(e.Size == LinkSize ? $"link became a normal file/folder: {path}" : $"file became a link: {path}");
            else if (e.Size == LinkSize && a.Hash != e.Hash) diff.Add($"link points somewhere else: {path}");
            else if (a.Size != e.Size) diff.Add($"different size: {path}");
            else if (a.Hash != e.Hash) diff.Add($"different content: {path}");
        }
        foreach (var path in actual.Keys.Where(k => !expected.ContainsKey(k))) diff.Add($"unexpected: {path}");
        return diff;
    }
}

public enum AlignStage { Copying, VerifyingCopy, Recreating, Restoring, VerifyingRestore, Done, Failed }

/// <summary>Saved after every step so an unfinished fix (crash, power cut) is noticed at the next start.</summary>
public sealed class AlignJob
{
    public char Letter { get; set; }
    public int DiskNumber { get; set; }
    public string BackupFolder { get; set; } = "";
    public AlignStage Stage { get; set; }
    public DateTime Started { get; set; } = DateTime.Now;
    public string? Error { get; set; }

    /// <summary>True once the original partition may have been deleted — from then on the backup is the only copy until the restore is verified.</summary>
    public bool DataOnlyInBackup => Stage is AlignStage.Recreating or AlignStage.Restoring or AlignStage.VerifyingRestore
                                    || (Stage == AlignStage.Failed && Error?.StartsWith("[after-delete]") == true);

    public static string FilePath => Path.Combine(DataPaths.Root, "align-job.json");
    public void Save() { try { File.WriteAllText(FilePath, Json.Serialize(this, indented: true)); } catch { } }
    public static AlignJob? Load() { try { return File.Exists(FilePath) ? Json.Deserialize<AlignJob>(File.ReadAllText(FilePath)) : null; } catch { return null; } }
    public static void Clear() { try { File.Delete(FilePath); } catch { } }
}

/// <summary>The Windows-specific steps, so the flow itself can be tested with a fake.</summary>
public interface IAlignOps
{
    /// <summary>Copies a whole tree (attributes, timestamps, permissions where possible). False = some files could not be copied.</summary>
    Task<bool> CopyTree(string from, string to, Action<string> log, CancellationToken ct);
    /// <summary>Deletes the partition and creates the aligned one. Returns the new start offset, or null on failure.</summary>
    Task<long?> Recreate(PartitionEntry p, Action<string> log);
    /// <summary>Waits until the drive letter is usable again.</summary>
    Task<bool> WaitForDrive(char letter, TimeSpan timeout);
}

/// <summary>
/// Re-aligns a data partition: copy all files to another drive → prove the copy is identical → recreate the partition aligned →
/// copy back → prove again → remove the copy. Nothing is deleted unless the copy has been verified, and the copy is kept if anything goes wrong.
/// </summary>
public sealed class PartitionAligner
{
    private readonly IAlignOps _ops;
    private readonly Action<string> _log;
    private readonly Action<double, string> _progress;

    public PartitionAligner(IAlignOps ops, Action<string> log, Action<double, string> progress) { _ops = ops; _log = log; _progress = progress; }

    /// <summary>Folder the files go to on the backup drive.</summary>
    public static string BackupFolderFor(PartitionEntry p, string backupRoot) =>
        Path.Combine(backupRoot, $"BenchForge alignment backup of {p.DriveLetter} {DateTime.Now:yyyy-MM-dd HHmm}");

    /// <summary>
    /// Runs the whole fix. <paramref name="confirmDelete"/> is asked once the copy is verified, right before the partition is deleted;
    /// <paramref name="confirmRemoveBackup"/> at the end. Cancelling only works before the partition is deleted.
    /// </summary>
    public async Task<bool> RunAsync(PartitionEntry p, string driveRoot, string backupFolder, Func<string, Task<bool>> confirmDelete, Func<string, Task<bool>> confirmRemoveBackup, CancellationToken ct)
    {
        var job = new AlignJob { Letter = p.DriveLetter ?? '?', DiskNumber = p.DiskNumber, BackupFolder = backupFolder, Stage = AlignStage.Copying };
        job.Save();
        Dictionary<string, (long Size, ulong Hash)> original;
        try
        {
            // 1. Copy everything off.
            _progress(0.02, "Copying files to the backup drive…");
            _log($"Step 1/5 — copying all files from {p.Name} to {backupFolder}");
            Directory.CreateDirectory(backupFolder);
            if (!await _ops.CopyTree(driveRoot, backupFolder, _log, ct)) return Fail(job, "Some files could not be copied (in use or unreadable). Nothing was changed on " + p.Name + ". Close programs using it and try again.");

            // 2. Prove the copy is identical.
            job.Stage = AlignStage.VerifyingCopy; job.Save();
            _progress(0.3, "Checking every copied file…");
            _log("Step 2/5 — checking every file against the original (fingerprint of each file)");
            long used = Math.Max(1, p.UsedBytes ?? 1);
            original = await Task.Run(() => FileManifest.Build(driveRoot, b => _progress(0.3 + 0.1 * Math.Min(1, b / (double)used), "Reading the original files…"), ct), ct);
            var copy = await Task.Run(() => FileManifest.Build(backupFolder, b => _progress(0.4 + 0.1 * Math.Min(1, b / (double)used), "Reading the copy…"), ct), ct);
            var diff = FileManifest.Compare(original, copy);
            if (diff.Count > 0)
            {
                foreach (var d in diff.Take(20)) _log("  " + d);
                return Fail(job, $"The copy is not identical ({diff.Count} differences — files may have changed while copying). Nothing was changed on {p.Name}.");
            }
            _log($"  OK — {original.Count:N0} files, {original.Values.Sum(v => v.Size) / 1e9:0.00} GB, all identical.");
        }
        catch (OperationCanceledException) { return Fail(job, $"Cancelled. Nothing was changed on {p.Name}."); }
        catch (Exception ex) { return Fail(job, $"Could not copy or check the files ({ex.Message}). Nothing was changed on {p.Name}. Close programs using it and try again."); }

        // 3. Point of no return — ask once more.
        if (!await confirmDelete($"All {original.Count:N0} files are safely copied and checked.\n\nBenchForge will now DELETE {p.Name} and create it again, aligned, then copy the files back.\nClose every program that uses {p.Name} first.\n\nContinue?"))
            return Fail(job, $"Stopped before changing anything. {p.Name} is untouched.");

        job.Stage = AlignStage.Recreating; job.Save();
        _progress(0.55, "Recreating the partition aligned…");
        _log($"Step 3/5 — deleting and recreating {p.Name} aligned to 1 MB");
        var newOffset = await _ops.Recreate(p, _log);
        if (newOffset is not long off || off % PartitionAlign.PageSize != 0 || !await _ops.WaitForDrive(p.DriveLetter!.Value, TimeSpan.FromSeconds(60)))
            return Fail(job, $"[after-delete] Recreating {p.Name} failed. YOUR FILES ARE SAFE in {backupFolder}. If {p.Name} still shows your files, nothing was changed. Otherwise open Disk Management, create a new volume in the free space with letter {p.DriveLetter}, then use 'Finish restoring' in BenchForge.");
        _log($"  New partition starts at {off:N0} bytes — aligned.");
        return await RestoreAsync(job, original, driveRoot, confirmRemoveBackup);
    }

    /// <summary>Copies the backup back and verifies it. Also used to finish a job that was interrupted after the partition was recreated.</summary>
    public async Task<bool> RestoreAsync(AlignJob job, Dictionary<string, (long Size, ulong Hash)>? expected, string driveRoot, Func<string, Task<bool>> confirmRemoveBackup)
    {
        job.Stage = AlignStage.Restoring; job.Save();
        _progress(0.6, "Copying files back…");
        _log($"Step 4/5 — copying the files back to {job.Letter}:");
        if (!await _ops.CopyTree(job.BackupFolder, driveRoot, _log, CancellationToken.None))
            return Fail(job, $"[after-delete] Some files could not be copied back. YOUR FILES ARE SAFE in {job.BackupFolder} — do not delete that folder. Try 'Finish restoring' again.");

        job.Stage = AlignStage.VerifyingRestore; job.Save();
        _progress(0.85, "Checking every restored file…");
        _log("Step 5/5 — checking every restored file");
        List<string> diff;
        Dictionary<string, (long Size, ulong Hash)> restored;
        try
        {
            expected ??= await Task.Run(() => FileManifest.Build(job.BackupFolder));
            restored = await Task.Run(() => FileManifest.Build(driveRoot));
            diff = FileManifest.Compare(expected, restored);
        }
        catch (Exception ex)
        {
            return Fail(job, $"[after-delete] Could not check the restored files ({ex.Message}). YOUR FILES ARE SAFE in {job.BackupFolder} — do not delete it. Try 'Finish restoring' again.");
        }
        if (diff.Count > 0)
        {
            foreach (var d in diff.Take(20)) _log("  " + d);
            return Fail(job, $"[after-delete] The restored files do not match ({diff.Count} differences). YOUR FILES ARE SAFE in {job.BackupFolder} — do not delete it. Try 'Finish restoring' again.");
        }
        _log($"  OK — {restored.Count:N0} files restored and identical.");
        job.Stage = AlignStage.Done; job.Save();
        _progress(1, "Done");

        if (await confirmRemoveBackup($"Done — {job.Letter}: is aligned and every file is back and checked.\n\nDelete the temporary copy in\n{job.BackupFolder}?"))
        {
            try { Directory.Delete(job.BackupFolder, true); _log("Temporary copy deleted."); }
            catch (Exception ex) { _log($"Could not delete the temporary copy ({ex.Message}) — you can delete {job.BackupFolder} yourself."); }
        }
        else _log($"Temporary copy kept in {job.BackupFolder}.");
        AlignJob.Clear();
        return true;
    }

    private bool Fail(AlignJob job, string message)
    {
        _log(message.Replace("[after-delete] ", ""));
        job.Error = message;
        bool keepJob = job.DataOnlyInBackup || message.StartsWith("[after-delete]");
        job.Stage = AlignStage.Failed;
        if (keepJob) { job.Save(); return false; }
        // The original is untouched, so the (partial) copy is not needed.
        AlignJob.Clear();
        try { if (Directory.Exists(job.BackupFolder)) { Directory.Delete(job.BackupFolder, true); _log("Temporary copy removed."); } }
        catch (Exception ex) { _log($"Could not remove the temporary copy ({ex.Message}) — delete {job.BackupFolder} yourself."); }
        return false;
    }
}
