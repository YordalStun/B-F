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
    /// <summary>Things tied to this exact volume that a recreate would break (Store apps, sync folders, mount points…), as reasons to show.</summary>
    public List<string> Blockers { get; set; } = new();

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
        if (p.Blockers.Count > 0) return new(false, p.Blockers[0]);
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

    /// <summary>Printed by the recreate script right after the old partition is gone — from then on the backup is the only copy.</summary>
    public const string RemovedMarker = "BF-REMOVED";
    public const string OffsetMarker = "NEWOFFSET=";
    public const string ErrorMarker = "BF-ERROR=";

    /// <summary>Wraps a script so errors come back as one plain line and progress bars don't flood the output.</summary>
    private static string Wrap(string body) =>
        "$ErrorActionPreference = 'Stop'\n$ProgressPreference = 'SilentlyContinue'\ntry {\n" + body + "} catch {\n  Write-Output ('" + ErrorMarker + "' + $_.Exception.Message)\n  exit 1\n}\n";

    /// <summary>
    /// PowerShell that deletes the partition and creates the aligned one in the same place, keeping letter, type, file system and label.
    /// The partition is looked up again by drive letter and must still be on the same disk, at the same offset and size —
    /// disk/partition numbers can change when drives are plugged in or out, so they are never trusted on their own.
    /// </summary>
    public static string RecreateScript(PartitionEntry p)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"$old = Get-Partition -DriveLetter {p.DriveLetter}");
        sb.AppendLine($"if ($old.DiskNumber -ne {p.DiskNumber} -or $old.Offset -ne {p.Offset} -or $old.Size -ne {p.Size}) {{ throw ('The partition changed since it was checked (disk ' + $old.DiskNumber + ', offset ' + $old.Offset + ', size ' + $old.Size + '). Nothing was deleted.') }}");
        sb.AppendLine("$old | Remove-Partition -Confirm:$false");
        sb.AppendLine($"Write-Output '{RemovedMarker}'");
        AppendCreate(sb, p);
        return Wrap(sb.ToString());
    }

    /// <summary>
    /// For finishing an interrupted fix: creates the aligned partition in the space the old one used — only if that space is
    /// completely free and the drive letter is unused, so it can never overwrite anything.
    /// </summary>
    public static string RecreateMissingScript(PartitionEntry p)
    {
        var (offset, size) = NewLayout(p.Offset, p.Size);
        var sb = new StringBuilder();
        sb.AppendLine($"if (Get-Partition | Where-Object {{ $_.DriveLetter -eq '{p.DriveLetter}' }}) {{ throw 'Drive letter {p.DriveLetter} is in use. Nothing was changed.' }}");
        sb.AppendLine($"$busy = Get-Partition -DiskNumber {p.DiskNumber} | Where-Object {{ ($_.Offset -lt {offset + size}) -and (($_.Offset + $_.Size) -gt {offset}) }}");
        sb.AppendLine("if ($busy) { throw 'That space on the disk is not free any more. Nothing was changed.' }");
        AppendCreate(sb, p);
        return Wrap(sb.ToString());
    }

    private static void AppendCreate(StringBuilder sb, PartitionEntry p)
    {
        var (offset, size) = NewLayout(p.Offset, p.Size);
        string Q(string s) => "'" + s.Replace("'", "''") + "'";
        // New-Partition only knows FAT12/FAT16/Huge/IFS/FAT32 (1/4/6/7/12): FAT32 LBA (0x0B/0x0C) → 12, FAT16 LBA (0x0E) → 6.
        int mbr = p.MbrType switch { 0x0B or 0x0C => 12, 0x0E => 6, _ => 7 };
        var type = p.PartitionStyle == "GPT" ? $"-GptType {Q(p.GptType ?? GptBasicData)}" : $"-MbrType {mbr}";
        var fs = p.FileSystem.Equals("FAT32", StringComparison.OrdinalIgnoreCase) ? "FAT32" : p.FileSystem.Equals("exFAT", StringComparison.OrdinalIgnoreCase) ? "exFAT" : "NTFS";
        sb.AppendLine("Update-Disk -Number " + p.DiskNumber);
        sb.AppendLine($"$p = New-Partition -DiskNumber {p.DiskNumber} -Offset {offset} -Size {size} -DriveLetter {p.DriveLetter} {type}");
        sb.AppendLine($"Format-Volume -Partition $p -FileSystem {fs} -NewFileSystemLabel {Q(p.Label)} -Confirm:$false -Force | Out-Null");
        sb.AppendLine($"Write-Output ('{OffsetMarker}' + $p.Offset)");
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

    /// <summary>Size and last-write time of every file (cheap — no reading), to notice changes made after the copy was checked.</summary>
    public static Dictionary<string, (long Size, long Ticks)> Stats(string root)
    {
        var map = new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, link) in Walk(root))
        {
            var rel = Path.GetRelativePath(root, file);
            if (link is not null) { map[rel] = (LinkSize, (long)XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(link))); continue; }
            var fi = new FileInfo(file);
            map[rel] = (fi.Length, fi.LastWriteTimeUtc.Ticks);
        }
        return map;
    }

    /// <summary>Files Windows encrypts per user (EFS). They can't be copied faithfully onto every drive, so the fix refuses them.</summary>
    public static List<string> FindEncrypted(string root, int max = 5)
    {
        var list = new List<string>();
        foreach (var (file, link) in Walk(root))
        {
            if (link is null && File.GetAttributes(file).HasFlag(FileAttributes.Encrypted)) list.Add(Path.GetRelativePath(root, file));
            if (list.Count >= max) break;
        }
        return list;
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

/// <summary>Deletes a folder tree without ever following links into other folders, and clears read-only flags robocopy may have copied.</summary>
public static class SafeDelete
{
    public static void Tree(string path)
    {
        var info = new DirectoryInfo(path);
        if (!info.Exists) return;
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) { info.Delete(); return; } // the link itself, never its target
        foreach (var f in info.EnumerateFiles())
        {
            if (f.Attributes.HasFlag(FileAttributes.ReadOnly)) f.Attributes &= ~FileAttributes.ReadOnly;
            f.Delete();
        }
        foreach (var d in info.EnumerateDirectories()) Tree(d.FullName);
        if (info.Attributes.HasFlag(FileAttributes.ReadOnly)) info.Attributes &= ~FileAttributes.ReadOnly;
        info.Delete();
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
    /// <summary>The partition as it was before the fix, so an interrupted fix can recreate it.</summary>
    public PartitionEntry? Partition { get; set; }
    /// <summary>BenchForge created the backup folder (so it may delete it). Never true for a folder that already existed.</summary>
    public bool OwnsBackupFolder { get; set; }

    /// <summary>True once the original partition may have been deleted — from then on the backup is the only copy until the restore is verified.</summary>
    public bool DataOnlyInBackup => Stage is AlignStage.Recreating or AlignStage.Restoring or AlignStage.VerifyingRestore
                                    || (Stage == AlignStage.Failed && Error?.StartsWith("[after-delete]") == true);

    public static string FilePath => Path.Combine(DataPaths.Root, "align-job.json");
    public void Save() { try { File.WriteAllText(FilePath, Json.Serialize(this, indented: true)); } catch { } }
    public static AlignJob? Load() { try { return File.Exists(FilePath) ? Json.Deserialize<AlignJob>(File.ReadAllText(FilePath)) : null; } catch { return null; } }
    public static void Clear() { try { File.Delete(FilePath); } catch { } }
}

/// <param name="Offset">Start of the new partition, or null if it was not created.</param>
/// <param name="Deleted">The old partition is gone (from here on the backup is the only copy).</param>
public sealed record RecreateResult(long? Offset, bool Deleted, string? Error = null);

/// <summary>The Windows-specific steps, so the flow itself can be tested with a fake.</summary>
public interface IAlignOps
{
    /// <summary>Copies a whole tree (attributes, timestamps, permissions where possible). False = some files could not be copied.</summary>
    Task<bool> CopyTree(string from, string to, Action<string> log, CancellationToken ct);
    /// <summary>Deletes the partition and creates the aligned one.</summary>
    Task<RecreateResult> Recreate(PartitionEntry p, Action<string> log);
    /// <summary>Creates the aligned partition in free space (finishing an interrupted fix). Never touches existing partitions.</summary>
    Task<RecreateResult> RecreateMissing(PartitionEntry p, Action<string> log);
    /// <summary>Locks and dismounts the volume so no program can still be writing to it. Returns null when done, or why it could not.</summary>
    Task<string?> LockAndDismount(char letter, Action<string> log);
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
    /// <summary>Room kept free on the new partition beyond the files' full size.</summary>
    public const long FitMargin = 256 * PartitionAlign.MiB;

    public PartitionAligner(IAlignOps ops, Action<string> log, Action<double, string> progress) { _ops = ops; _log = log; _progress = progress; }

    /// <summary>Folder the files go to on the backup drive. Always a new name, never an existing folder.</summary>
    public static string BackupFolderFor(PartitionEntry p, string backupRoot)
    {
        var baseName = Path.Combine(backupRoot, $"BenchForge alignment backup of {p.DriveLetter} {DateTime.Now:yyyy-MM-dd HHmmss}");
        var path = baseName;
        for (int i = 2; Directory.Exists(path) || File.Exists(path); i++) path = $"{baseName} ({i})";
        return path;
    }

    /// <summary>
    /// Runs the whole fix. <paramref name="confirmDelete"/> is asked once the copy is verified, right before the partition is deleted;
    /// <paramref name="confirmRemoveBackup"/> at the end. Cancelling only works before the partition is deleted.
    /// With <paramref name="practice"/> it stops after the copy is checked: nothing on the drive is ever changed.
    /// </summary>
    public async Task<bool> RunAsync(PartitionEntry p, string driveRoot, string backupFolder, Func<string, Task<bool>> confirmDelete, Func<string, Task<bool>> confirmRemoveBackup, CancellationToken ct, bool practice = false)
    {
        if (Directory.Exists(backupFolder) || File.Exists(backupFolder))
        {
            _log($"The folder {backupFolder} already exists — BenchForge only uses a new, empty folder. Nothing was changed.");
            return false;
        }
        var job = new AlignJob { Letter = p.DriveLetter ?? '?', DiskNumber = p.DiskNumber, BackupFolder = backupFolder, Stage = AlignStage.Copying, Partition = p, OwnsBackupFolder = true };
        if (!practice) job.Save();
        Dictionary<string, (long Size, ulong Hash)> original;
        Dictionary<string, (long Size, long Ticks)> stats;
        try
        {
            var encrypted = await Task.Run(() => FileManifest.FindEncrypted(driveRoot), ct);
            if (encrypted.Count > 0)
                return Fail(job, $"{p.Name} has files encrypted with Windows file encryption (EFS), e.g. {encrypted[0]}. They can't be moved safely this way — use a partition tool. Nothing was changed.");

            // 1. Copy everything off.
            _progress(0.02, "Copying files to the backup drive…");
            _log($"Step 1/5 — copying all files from {p.Name} to {backupFolder}");
            Directory.CreateDirectory(backupFolder);
            if (!await _ops.CopyTree(driveRoot, backupFolder, _log, ct)) return Fail(job, "Some files could not be copied (in use or unreadable). Nothing was changed on " + p.Name + ". Close programs using it and try again.");

            // 2. Prove the copy is identical.
            job.Stage = AlignStage.VerifyingCopy; if (!practice) job.Save();
            _progress(0.3, "Checking every copied file…");
            _log("Step 2/5 — checking every file against the original (fingerprint of each file)");
            long used = Math.Max(1, p.UsedBytes ?? 1);
            stats = await Task.Run(() => FileManifest.Stats(driveRoot), ct);
            original = await Task.Run(() => FileManifest.Build(driveRoot, b => _progress(0.3 + 0.1 * Math.Min(1, b / (double)used), "Reading the original files…"), ct), ct);
            var copy = await Task.Run(() => FileManifest.Build(backupFolder, b => _progress(0.4 + 0.1 * Math.Min(1, b / (double)used), "Reading the copy…"), ct), ct);
            var diff = FileManifest.Compare(original, copy);
            if (diff.Count > 0)
            {
                foreach (var d in diff.Take(20)) _log("  " + d);
                return Fail(job, $"The copy is not identical ({diff.Count} differences — files may have changed while copying). Nothing was changed on {p.Name}.");
            }
            long total = original.Values.Where(v => v.Size > 0).Sum(v => v.Size);
            _log($"  OK — {original.Count:N0} files, {total / 1e9:0.00} GB, all identical.");

            // Compressed, sparse and hard-linked files can take more room once copied back: make sure everything fits.
            var (_, newSize) = PartitionAlign.NewLayout(p.Offset, p.Size);
            if (total + FitMargin > newSize)
                return Fail(job, $"The files need {total / 1e9:0.0} GB when copied back (compressed or sparse files take their full size), but the new partition holds {newSize / 1e9:0.0} GB. Free some space on {p.Name} first. Nothing was changed.");
        }
        catch (OperationCanceledException) { return Fail(job, $"Cancelled. Nothing was changed on {p.Name}."); }
        catch (Exception ex) { return Fail(job, $"Could not copy or check the files ({ex.Message}). Nothing was changed on {p.Name}. Close programs using it and try again."); }

        if (practice)
        {
            _progress(1, "Practice run finished");
            _log($"Practice run OK — every file on {p.Name} could be copied and checked. Nothing on {p.Name} was changed.");
            RemoveBackup(job);
            return true;
        }

        // 3. Point of no return — ask once more.
        if (!await confirmDelete($"All {original.Count:N0} files are safely copied and checked.\n\nBenchForge will now DELETE {p.Name} and create it again, aligned, then copy the files back.\nClose every program that uses {p.Name} first.\n\nContinue?"))
            return Fail(job, $"Stopped before changing anything. {p.Name} is untouched.");

        // Nothing may have changed since the check, and nothing may still have files open.
        try
        {
            var now = await Task.Run(() => FileManifest.Stats(driveRoot));
            var changed = now.Where(kv => !stats.TryGetValue(kv.Key, out var s) || s != kv.Value).Select(kv => kv.Key)
                .Concat(stats.Keys.Where(k => !now.ContainsKey(k))).ToList();
            if (changed.Count > 0)
                return Fail(job, $"Files on {p.Name} changed after they were copied (e.g. {changed[0]}). Close the program using it and run the fix again. Nothing was changed.");
        }
        catch (Exception ex) { return Fail(job, $"Could not re-check {p.Name} ({ex.Message}). Nothing was changed."); }
        var lockError = await _ops.LockAndDismount(p.DriveLetter!.Value, _log);
        if (lockError != null) return Fail(job, $"{lockError} Nothing was changed on {p.Name}.");

        job.Stage = AlignStage.Recreating; job.Save();
        _progress(0.55, "Recreating the partition aligned…");
        _log($"Step 3/5 — deleting and recreating {p.Name} aligned to 1 MB");
        var r = await _ops.Recreate(p, _log);
        // Only trust "not deleted" if the drive is really still there; otherwise treat it as deleted and keep the copy.
        if (!r.Deleted && await _ops.WaitForDrive(job.Letter, TimeSpan.FromSeconds(10)))
        {
            job.Stage = AlignStage.VerifyingCopy;
            return Fail(job, $"{p.Name} was not changed: {r.Error ?? "the partition could not be deleted"}.");
        }
        return await AfterRecreate(job, r, original, driveRoot, confirmRemoveBackup);
    }

    private async Task<bool> AfterRecreate(AlignJob job, RecreateResult r, Dictionary<string, (long Size, ulong Hash)>? original, string driveRoot, Func<string, Task<bool>> confirmRemoveBackup)
    {
        if (r.Offset is not long off || off % PartitionAlign.PageSize != 0 || !await _ops.WaitForDrive(job.Letter, TimeSpan.FromSeconds(60)))
            return Fail(job, $"[after-delete] Creating the new {job.Letter}: failed ({r.Error ?? "drive did not appear"}). YOUR FILES ARE SAFE in {job.BackupFolder}. Use 'Finish restoring' in BenchForge's alignment window.");
        _log($"  New partition starts at {off:N0} bytes — aligned.");
        return await RestoreAsync(job, original, driveRoot, confirmRemoveBackup);
    }

    /// <summary>Finishes a fix that stopped after the old partition was deleted: recreates it if it's missing, then restores and checks.</summary>
    public async Task<bool> ResumeAsync(AlignJob job, string driveRoot, bool driveExists, Func<string, Task<bool>> confirmRemoveBackup)
    {
        if (!Directory.Exists(job.BackupFolder)) { _log($"The temporary copy {job.BackupFolder} was not found."); return false; }
        if (driveExists) return await RestoreAsync(job, null, driveRoot, confirmRemoveBackup);
        if (job.Partition is null) { _log("This fix was started by an older BenchForge — create the volume in Disk Management, then press 'Finish restoring'."); return false; }
        job.Stage = AlignStage.Recreating; job.Save();
        _log($"Creating {job.Letter}: again in its old place on disk {job.DiskNumber} (only if that space is still free)…");
        var r = await _ops.RecreateMissing(job.Partition, _log);
        if (r.Offset is null) return Fail(job, $"[after-delete] Could not create {job.Letter}: ({r.Error}). YOUR FILES ARE SAFE in {job.BackupFolder}.");
        return await AfterRecreate(job, r, null, driveRoot, confirmRemoveBackup);
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
            RemoveBackup(job);
        else _log($"Temporary copy kept in {job.BackupFolder}.");
        AlignJob.Clear();
        return true;
    }

    private void RemoveBackup(AlignJob job)
    {
        if (!job.OwnsBackupFolder || !Directory.Exists(job.BackupFolder)) return;
        try { SafeDelete.Tree(job.BackupFolder); _log("Temporary copy deleted."); }
        catch (Exception ex) { _log($"Could not delete the temporary copy ({ex.Message}) — you can delete {job.BackupFolder} yourself."); }
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
        RemoveBackup(job);
        return false;
    }
}
