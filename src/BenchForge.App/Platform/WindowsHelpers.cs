using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BenchForge.Core.Model;

namespace BenchForge.App.Platform;

public static class Secrets
{
    public static string Protect(string plain) => Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser));

    public static string? Unprotect(string? encrypted)
    {
        if (string.IsNullOrEmpty(encrypted)) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(encrypted), null, DataProtectionScope.CurrentUser)); }
        catch { return null; } // e.g. settings copied from another PC/user: key must be entered again
    }
}

/// <summary>Registers a one-off logon task so BenchForge reopens with the crash report if the PC goes down mid-test.</summary>
public static class AutoReopen
{
    private const string TaskName = "BenchForge crash report";

    public static void Enable()
    {
        var exe = Environment.ProcessPath;
        if (exe == null) return;
        Run("schtasks", $"/Create /TN \"{TaskName}\" /TR \"\\\"{exe}\\\" --after-crash\" /SC ONLOGON /RL HIGHEST /F");
    }

    public static void Disable() => Run("schtasks", $"/Delete /TN \"{TaskName}\" /F");

    private static void Run(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
            p?.WaitForExit(5000);
        }
        catch { }
    }
}

/// <summary>Optional deep crash-dump analysis with Microsoft's debugger (cdb.exe from the Windows SDK), if installed.</summary>
public static class DumpAnalyzer
{
    public static string? FindCdb()
    {
        var candidates = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) + @"\Windows Kits\10\Debuggers\x64\cdb.exe",
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + @"\Windows Kits\10\Debuggers\x64\cdb.exe",
            Path.Combine(AppContext.BaseDirectory, "Debuggers", "cdb.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    public static async Task<string> AnalyzeAsync(MinidumpInfo dump, CancellationToken ct)
    {
        var cdb = FindCdb() ?? throw new InvalidOperationException("Debugging Tools for Windows (cdb.exe) are not installed.");
        var symbols = Path.Combine(Core.Persistence.DataPaths.Root, "Symbols");
        var psi = new ProcessStartInfo(cdb, $"-z \"{dump.Path}\" -y \"srv*{symbols}*https://msdl.microsoft.com/download/symbols\" -c \"!analyze -v; q\"")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
        var output = await outTask;
        var fields = Core.Crash.CdbOutput.Parse(output);
        dump.FaultingModule = fields.GetValueOrDefault("IMAGE_NAME") ?? fields.GetValueOrDefault("MODULE_NAME");
        dump.DeepAnalysis = string.Join(" · ", fields.Select(kv => $"{kv.Key}: {kv.Value}"));
        return output;
    }
}
