using System.Globalization;
using System.Management;
using System.Text;
using BenchForge.Core.Model;
using BenchForge.Core.Storage;

namespace BenchForge.App.Platform;

/// <summary>Reads every partition with what <see cref="PartitionAlign.Check"/> needs (type, page file, BitLocker…).</summary>
public static class PartitionReader
{
    private const string StorageNs = @"root\Microsoft\Windows\Storage";

    public static List<PartitionEntry> Read(HardwareInventory? inv)
    {
        var disks = new Dictionary<int, (string Style, string Name)>();
        foreach (var d in Query("SELECT Number, PartitionStyle, FriendlyName FROM MSFT_Disk", StorageNs))
            disks[Int(d, "Number")] = (Int(d, "PartitionStyle") switch { 1 => "MBR", 2 => "GPT", _ => "RAW" }, Str(d, "FriendlyName"));

        var volumes = new Dictionary<char, ManagementBaseObject>();
        foreach (var v in Query("SELECT DriveLetter, FileSystem, FileSystemLabel, Size, SizeRemaining FROM MSFT_Volume", StorageNs))
            if (Letter(v["DriveLetter"]) is char c) volumes[c] = v;

        var pageFiles = new HashSet<char>();
        try
        {
            foreach (var pf in Query("SELECT Name FROM Win32_PageFileUsage"))
                if (Str(pf, "Name") is { Length: > 1 } n && n[1] == ':') pageFiles.Add(char.ToUpperInvariant(n[0]));
        }
        catch { /* treat as none */ }

        var encryption = ReadBitLocker();
        var list = new List<PartitionEntry>();
        var raw = Query("SELECT DiskNumber, PartitionNumber, DriveLetter, Offset, Size, IsSystem, IsBoot, IsHidden, GptType, MbrType FROM MSFT_Partition", StorageNs);
        foreach (var o in raw)
        {
            int disk = Int(o, "DiskNumber");
            var p = new PartitionEntry
            {
                DiskNumber = disk, PartitionNumber = Int(o, "PartitionNumber"), DriveLetter = Letter(o["DriveLetter"]),
                Offset = Long(o, "Offset"), Size = Long(o, "Size"),
                IsSystem = Bool(o, "IsSystem"), IsBoot = Bool(o, "IsBoot"), IsHidden = Bool(o, "IsHidden"),
                GptType = Str(o, "GptType") is { Length: > 0 } g ? g : null,
                MbrType = o["MbrType"] == null ? null : Int(o, "MbrType"),
            };
            if (disks.TryGetValue(disk, out var dk)) { p.PartitionStyle = dk.Style; p.DiskModel = dk.Name; }
            var info = inv?.Disks.FirstOrDefault(x => x.DiskNumber == disk);
            if (info != null) { p.MediaType = info.MediaType; if (p.DiskModel.Length == 0) p.DiskModel = info.Model; }
            if (p.DriveLetter is char c)
            {
                if (volumes.TryGetValue(c, out var v))
                {
                    p.FileSystem = Str(v, "FileSystem"); p.Label = Str(v, "FileSystemLabel");
                    long size = Long(v, "Size"), free = Long(v, "SizeRemaining");
                    p.UsedBytes = size > 0 ? size - free : null;
                }
                p.HasPageFile = pageFiles.Contains(c);
                p.Encrypted = encryption == null ? null : encryption.TryGetValue(c, out var e) ? e : false;
            }
            list.Add(p);
        }
        // MBR logical partitions live inside an extended container (type 0x05 / 0x0F).
        foreach (var ext in list.Where(x => x.MbrType is 5 or 15).ToList())
            foreach (var p in list.Where(x => x.DiskNumber == ext.DiskNumber && x != ext && x.Offset >= ext.Offset && x.Offset < ext.Offset + ext.Size))
                p.IsLogical = true;
        return list.OrderBy(x => x.DiskNumber).ThenBy(x => x.Offset).ToList();
    }

    /// <summary>Drives that could hold the temporary copy.</summary>
    public static List<BackupVolume> Volumes(IReadOnlyList<PartitionEntry> parts) =>
        DriveInfo.GetDrives()
            .Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable && d.Name.Length >= 2)
            .Select(d =>
            {
                char c = char.ToUpperInvariant(d.Name[0]);
                return new BackupVolume(c, d.DriveFormat, d.AvailableFreeSpace, parts.FirstOrDefault(p => p.DriveLetter == c)?.DiskNumber, d.VolumeLabel);
            }).ToList();

    /// <summary>Letter → encrypted. null = could not check (not admin). Empty when BitLocker isn't installed (Home editions without device encryption).</summary>
    private static Dictionary<char, bool>? ReadBitLocker()
    {
        var map = new Dictionary<char, bool>();
        try
        {
            using var s = new ManagementObjectSearcher(@"root\CIMV2\Security\MicrosoftVolumeEncryption", "SELECT DriveLetter, ProtectionStatus FROM Win32_EncryptableVolume");
            foreach (ManagementObject v in s.Get())
            {
                if (Str(v, "DriveLetter") is not { Length: > 0 } dl) continue;
                bool on = Int(v, "ProtectionStatus") == 1;
                try
                {
                    // ConversionStatus 0 = fully decrypted; anything else means data on disk is (partly) encrypted.
                    var r = v.InvokeMethod("GetConversionStatus", null, null);
                    if (r != null && Convert.ToInt32(r["ConversionStatus"]) != 0) on = true;
                }
                catch { }
                map[char.ToUpperInvariant(dl[0])] = on;
            }
            return map;
        }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.InvalidNamespace) { return map; }
        catch { return null; }
    }

    private static char? Letter(object? o) => o switch
    {
        char c when char.IsLetter(c) => char.ToUpperInvariant(c),
        ushort u when u != 0 && char.IsLetter((char)u) => char.ToUpperInvariant((char)u),
        string s when s.Length > 0 && char.IsLetter(s[0]) => char.ToUpperInvariant(s[0]),
        _ => null,
    };

    private static List<ManagementBaseObject> Query(string wql, string scope = @"root\cimv2")
    {
        using var s = new ManagementObjectSearcher(scope, wql);
        return s.Get().Cast<ManagementBaseObject>().ToList();
    }
    private static string Str(ManagementBaseObject o, string p) { try { return o[p]?.ToString() ?? ""; } catch { return ""; } }
    private static int Int(ManagementBaseObject o, string p) { try { return o[p] == null ? 0 : Convert.ToInt32(o[p], CultureInfo.InvariantCulture); } catch { return 0; } }
    private static long Long(ManagementBaseObject o, string p) { try { return o[p] == null ? 0 : Convert.ToInt64(o[p], CultureInfo.InvariantCulture); } catch { return 0; } }
    private static bool Bool(ManagementBaseObject o, string p) { try { return o[p] is bool b && b; } catch { return false; } }
}

/// <summary>The real Windows steps: robocopy for copying, the Storage PowerShell module for recreating the partition.</summary>
public sealed class WindowsAlignOps : IAlignOps
{
    public async Task<bool> CopyTree(string from, string to, Action<string> log, CancellationToken ct)
    {
        Directory.CreateDirectory(to);
        // Keep permissions (S) and owner (O) only when both sides are NTFS; exFAT/FAT can't store them.
        // /SJ /SL copy junctions and symlinks as links (never followed, never dropped).
        bool acl = IsNtfs(from) && IsNtfs(to);
        var src = Arg(from); var dst = Arg(to);
        var skip = string.Join(" ", FileManifest.Skip.Select(s => Arg(Path.Combine(from, s))));
        var args = $"{src} {dst} /E /COPY:{(acl ? "DATSO" : "DAT")} /DCOPY:DAT /B /SJ /SL /R:2 /W:1 /MT:8 /NP /NFL /NDL /XD {skip}";
        log("  robocopy " + args);
        int code = await FixRunner.RunProcess("robocopy.exe", args, line => { if (!string.IsNullOrWhiteSpace(line)) log("  " + line); }, ct, Encoding.UTF8);
        // Copying from a drive root makes robocopy mark the destination folder hidden + system; undo that.
        try { if (Path.GetPathRoot(to) != to.TrimEnd('\\') + "\\") { var di = new DirectoryInfo(to); di.Attributes &= ~(FileAttributes.Hidden | FileAttributes.System); } } catch { }
        // Robocopy: 0–7 = success (1 = files copied), 8+ = some files failed.
        return code < 8;
    }

    public async Task<long?> Recreate(PartitionEntry p, Action<string> log)
    {
        var script = PartitionAlign.RecreateScript(p);
        foreach (var line in script.Split('\n', StringSplitOptions.RemoveEmptyEntries)) log("  PS> " + line.TrimEnd());
        long? offset = null;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        int code = await FixRunner.RunProcess("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}", line =>
        {
            if (line.StartsWith("NEWOFFSET=") && long.TryParse(line[10..], out var o)) offset = o;
            else log("  " + line);
        }, CancellationToken.None, Encoding.UTF8);
        return code == 0 ? offset : null;
    }

    public async Task<bool> WaitForDrive(char letter, TimeSpan timeout)
    {
        var end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end)
        {
            try { if (new DriveInfo($"{letter}:\\").IsReady) return true; } catch { }
            await Task.Delay(1000);
        }
        return false;
    }

    private static bool IsNtfs(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(path)!).DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase); } catch { return false; }
    }

    /// <summary>Quotes a path for robocopy. A trailing backslash would escape the closing quote, so roots become "D:\.".</summary>
    private static string Arg(string path)
    {
        var p = path.TrimEnd('\\');
        if (p.Length == 2 && p[1] == ':') p += "\\.";
        return "\"" + p + "\"";
    }
}
