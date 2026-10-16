using System.Diagnostics;
using System.Text;
using BenchForge.Core.Persistence;
using BenchForge.Core.Storage;

namespace BenchForge.App.Platform;

/// <summary>
/// End-to-end test of the partition-alignment fix on real Windows, run by CI (<c>BenchForge.exe --align-selftest &lt;dir&gt;</c>).
/// Creates throw-away virtual disks with deliberately misaligned partitions — never touches a real drive.
/// Exit code 0 = every check passed; the log is written to &lt;dir&gt;\align-selftest.log.
/// </summary>
public static class AlignSelfTest
{
    private static StreamWriter? _log;
    private static int _failures;

    public static int Run(string dir)
    {
        Directory.CreateDirectory(dir);
        DataPaths.Override(Path.Combine(dir, "data"));
        using (_log = new StreamWriter(Path.Combine(dir, "align-selftest.log"), false, Encoding.UTF8) { AutoFlush = true })
        {
            try
            {
                foreach (var style in new[] { "mbr", "gpt" }) RunOne(dir, style).GetAwaiter().GetResult();
            }
            catch (Exception ex) { Fail("Unexpected exception: " + ex); }
            Log(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
        }
        return _failures == 0 ? 0 : 1;
    }

    private static async Task RunOne(string dir, string style)
    {
        Log($"===== {style.ToUpperInvariant()} disk =====");
        // Classic .vhd (512-byte physical sectors): Windows refuses unaligned partitions on 4K-sector .vhdx disks.
        var vhd = Path.Combine(dir, $"bf-align-{style}.vhd");
        char letter = FreeLetter();
        // diskpart always rounds partition starts to an aligned boundary, so create the disk with diskpart and the
        // deliberately misaligned partition with PowerShell. MBR: sector 63 (32,256 bytes, the classic XP-era start);
        // GPT: 20 MB + 3.5 KB, past the reserved area. Neither is a multiple of 4,096.
        Diskpart($"""
            create vdisk file="{vhd}" maximum=1024 type=expandable
            select vdisk file="{vhd}"
            attach vdisk
            """);
        long offset = style == "mbr" ? 63 * 512 : 20L * 1024 * 1024 + 3584;
        PowerShell($$"""
            $ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue'
            $d = Get-Disk | Where-Object { $_.Location -eq '{{vhd}}' }
            Write-Output ('Disk ' + $d.Number + ': logical sector ' + $d.LogicalSectorSize + ' bytes, physical ' + $d.PhysicalSectorSize)
            Initialize-Disk -Number $d.Number -PartitionStyle {{style.ToUpperInvariant()}}
            $p = New-Partition -DiskNumber $d.Number -Offset {{offset}} -Size 700MB -DriveLetter {{letter}}
            Write-Output ('Partition created at offset ' + $p.Offset)
            Format-Volume -Partition $p -FileSystem NTFS -NewFileSystemLabel 'BF Test' -Confirm:$false -Force | Out-Null
            """);
        try
        {
            var root = $"{letter}:\\";
            await WaitReady(letter);
            Populate(root, dir);
            var before = FileManifest.Build(root);
            Log($"Created {before.Count} files/links on {letter}:");

            var part = PartitionReader.Read(null).FirstOrDefault(p => p.DriveLetter == letter);
            Check(part != null, $"partition {letter}: found");
            if (part == null) return;
            Check(!part.IsAligned, $"partition starts misaligned ({part.Offset:N0} bytes)");
            var verdict = PartitionAlign.Check(part, AppContext.BaseDirectory, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            Check(verdict.CanAutoFix, $"auto-fix allowed ({verdict.Reason})");

            var ops = new WindowsAlignOps();

            // A program holding a file open must stop the fix before anything is deleted.
            using (var open = new FileStream(Path.Combine(root, "notes.txt"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var err = await ops.LockAndDismount(letter, Log);
                Check(err != null, $"lock refused while a file is open ({err})");
            }

            // A partition that no longer matches what was checked must never be deleted.
            var stale = Clone(part); stale.Offset += 512;
            var guard = await ops.Recreate(stale, Log);
            Check(!guard.Deleted && guard.Offset == null, $"guard stops a stale partition ({guard.Error})");
            Check(Directory.Exists(root) && File.Exists(Path.Combine(root, "notes.txt")), "drive untouched after the guard");

            // Practice run: copies and checks, changes nothing.
            var backupRoot = Path.Combine(dir, "backups");
            Directory.CreateDirectory(backupRoot);
            var aligner = new PartitionAligner(ops, Log, (_, _) => { });
            bool practice = await aligner.RunAsync(part, root, PartitionAligner.BackupFolderFor(part, backupRoot), _ => Task.FromResult(false), _ => Task.FromResult(true), CancellationToken.None, practice: true);
            Check(practice, "practice run passes");
            var afterPractice = PartitionReader.Read(null).First(p => p.DriveLetter == letter);
            Check(afterPractice.Offset == part.Offset, "practice run left the partition alone");

            // The real fix.
            var folder = PartitionAligner.BackupFolderFor(part, backupRoot);
            bool ok = await aligner.RunAsync(part, root, folder, _ => Task.FromResult(true), _ => Task.FromResult(true), CancellationToken.None);
            Check(ok, "fix completed");
            var fixedPart = PartitionReader.Read(null).FirstOrDefault(p => p.DriveLetter == letter);
            Check(fixedPart?.IsAligned == true, $"partition aligned now (starts at {fixedPart?.Offset:N0})");
            Check(fixedPart?.Label == "BF Test" && fixedPart.FileSystem == "NTFS", "label and file system kept");
            var after = FileManifest.Build(root);
            var diff = FileManifest.Compare(before, after);
            foreach (var d in diff.Take(20)) Log("  diff: " + d);
            Check(diff.Count == 0, "every file and link identical after the fix");
            Check(File.ReadAllText(Path.Combine(root, "data", "stream.txt") + ":bfstream") == "alternate stream", "alternate data stream kept");
            Check(File.GetAttributes(Path.Combine(root, "data", "readonly.txt")).HasFlag(FileAttributes.ReadOnly), "read-only flag kept");
            Check(File.GetAttributes(Path.Combine(root, "data", "hidden.txt")).HasFlag(FileAttributes.Hidden), "hidden flag kept");
            Check(Directory.Exists(Path.Combine(root, "data", "empty-folder")), "empty folder kept");
            Check(new DirectoryInfo(Path.Combine(root, "junction")).LinkTarget != null, "junction kept as a link");
            Check(File.Exists(Path.Combine(dir, "outside", "outside.txt")), "link target outside the drive untouched");
            Check(!Directory.Exists(folder), "temporary copy removed");
            Check(AlignJob.Load() == null, "no unfinished job left behind");

            // Things tied to the drive must block the automatic fix.
            Directory.CreateDirectory(Path.Combine(root, "WindowsApps"));
            var blocked = PartitionReader.Read(null).First(p => p.DriveLetter == letter);
            blocked.Offset += 512; // pretend it is still misaligned
            var v2 = PartitionAlign.Check(blocked);
            Check(!v2.CanAutoFix && v2.Reason.Contains("Store"), $"Store-app folder blocks the fix ({v2.Reason})");
        }
        finally
        {
            try { Diskpart($"""
                select vdisk file="{vhd}"
                detach vdisk
                """); }
            catch (Exception ex) { Log("detach failed: " + ex.Message); }
        }
    }

    private static void Populate(string root, string dir)
    {
        var rnd = new Random(42);
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(Path.Combine(data, "nested", "deeper"));
        File.WriteAllText(Path.Combine(root, "notes.txt"), "hello");
        for (int i = 0; i < 40; i++)
        {
            var bytes = new byte[rnd.Next(1, 3_000_000)];
            rnd.NextBytes(bytes);
            File.WriteAllBytes(Path.Combine(data, i % 2 == 0 ? "nested" : Path.Combine("nested", "deeper"), $"file{i}.bin"), bytes);
        }
        File.WriteAllText(Path.Combine(data, "stream.txt"), "main");
        File.WriteAllText(Path.Combine(data, "stream.txt") + ":bfstream", "alternate stream");
        File.WriteAllText(Path.Combine(data, "readonly.txt"), "ro");
        File.SetAttributes(Path.Combine(data, "readonly.txt"), FileAttributes.ReadOnly);
        File.WriteAllText(Path.Combine(data, "hidden.txt"), "hidden");
        File.SetAttributes(Path.Combine(data, "hidden.txt"), FileAttributes.Hidden);
        Directory.CreateDirectory(Path.Combine(data, "empty-folder"));
        // Long path (> 260 characters).
        var longDir = Path.Combine(data, string.Join("\\", Enumerable.Repeat("a-rather-long-folder-name-for-testing", 8)));
        Directory.CreateDirectory(longDir);
        File.WriteAllText(Path.Combine(longDir, "deep.txt"), "deep");
        // A junction and a symlink pointing outside the drive (like a game library).
        var outside = Path.Combine(dir, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "outside.txt"), "must survive");
        Run("cmd.exe", $"/c mklink /J \"{Path.Combine(root, "junction")}\" \"{outside}\"");
        Directory.CreateSymbolicLink(Path.Combine(root, "symlink"), Path.Combine(root, "data"));
    }

    private static PartitionEntry Clone(PartitionEntry p) => Json.Deserialize<PartitionEntry>(Json.Serialize(p))!;

    private static char FreeLetter()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        for (char c = 'R'; c <= 'Y'; c++) if (!used.Contains(c)) return c;
        throw new InvalidOperationException("No free drive letter");
    }

    private static async Task WaitReady(char letter)
    {
        for (int i = 0; i < 60; i++)
        {
            try { if (new DriveInfo($"{letter}:\\").IsReady) return; } catch { }
            await Task.Delay(500);
        }
        throw new InvalidOperationException($"{letter}: did not appear");
    }

    private static void Diskpart(string script)
    {
        var file = Path.Combine(Path.GetTempPath(), $"bf-diskpart-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, script);
        try
        {
            var code = Run("diskpart.exe", $"/s \"{file}\"");
            if (code != 0) throw new InvalidOperationException($"diskpart failed ({code})");
        }
        finally { File.Delete(file); }
    }

    private static void PowerShell(string script)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var code = Run("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -OutputFormat Text -EncodedCommand {encoded}");
        if (code != 0) throw new InvalidOperationException($"PowerShell setup failed ({code})");
    }

    private static int Run(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        foreach (var line in output.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l))) Log("  " + line.TrimEnd());
        return p.ExitCode;
    }

    private static void Check(bool ok, string what)
    {
        Log((ok ? "PASS  " : "FAIL  ") + what);
        if (!ok) _failures++;
    }

    private static void Fail(string what) { Log("FAIL  " + what); _failures++; }
    private static void Log(string line) => _log?.WriteLine($"{DateTime.Now:HH:mm:ss} {line}");
}
