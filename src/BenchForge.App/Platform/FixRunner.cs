using System.Diagnostics;
using System.Text;
using BenchForge.Core.Analysis;
using Microsoft.Win32;

namespace BenchForge.App.Platform;

/// <summary>Applies the software fixes in <see cref="Fixes"/>. Every action is logged to the output callback.</summary>
public static class FixRunner
{
    public static async Task<bool> RunAsync(FixId id, Action<string> output, CancellationToken ct)
    {
        try
        {
            switch (id)
            {
                case FixId.RepairSystemFiles:
                    output("Step 1/2: DISM /Online /Cleanup-Image /RestoreHealth (this can take a while)…");
                    int dism = await RunProcess("DISM.exe", "/Online /Cleanup-Image /RestoreHealth", output, ct, Encoding.UTF8);
                    output(dism == 0 ? "DISM finished OK." : $"DISM ended with code {dism}.");
                    output("Step 2/2: sfc /scannow …");
                    int sfc = await RunProcess("sfc.exe", "/scannow", output, ct, Encoding.Unicode);
                    output(sfc == 0 ? "SFC finished. If it repaired files, restart the PC." : $"SFC ended with code {sfc}. See C:\\Windows\\Logs\\CBS\\CBS.log for details.");
                    return dism == 0 || sfc == 0;

                case FixId.ScanDisk:
                {
                    var drive = (Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\").TrimEnd('\\');
                    output($"chkdsk {drive} /scan …");
                    int code = await RunProcess("chkdsk.exe", $"{drive} /scan", output, ct, Encoding.UTF8);
                    output(code == 0 ? "No problems found on the drive." : "Problems were found. Use 'Repair at next restart' below (chkdsk /spotfix) or run 'chkdsk C: /f' and restart.");
                    return code == 0;
                }

                case FixId.MemoryDiagnostic:
                    output("Opening Windows Memory Diagnostic — choose when to restart.");
                    Process.Start(new ProcessStartInfo("mdsched.exe") { UseShellExecute = true });
                    return true;

                case FixId.PowerPlanBalanced:
                    int pc = await RunProcess("powercfg.exe", "/setactive SCHEME_BALANCED", output, ct, Encoding.UTF8);
                    output(pc == 0 ? "Power plan set to Balanced." : "Could not change the power plan.");
                    return pc == 0;

                case FixId.DisableFastStartup:
                    using (var k = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Power", true))
                        k.SetValue("HiberbootEnabled", 0, RegistryValueKind.DWord);
                    output("Fast Startup turned off.");
                    return true;

                case FixId.EnableCrashDumps:
                    using (var k = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Control\CrashControl", true))
                    {
                        k.SetValue("CrashDumpEnabled", 3, RegistryValueKind.DWord); // small memory dump
                        k.SetValue("MinidumpDir", @"%SystemRoot%\Minidump", RegistryValueKind.ExpandString);
                        k.SetValue("AutoReboot", 1, RegistryValueKind.DWord);
                    }
                    output("Windows will now save a small memory dump on every blue screen (C:\\Windows\\Minidump). Takes effect after a restart. A page file on C: is required for dumps.");
                    return true;

                case FixId.IncreaseTdrDelay:
                    using (var k = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", true))
                    {
                        k.SetValue("TdrDelay", 8, RegistryValueKind.DWord);
                        k.SetValue("TdrDdiDelay", 8, RegistryValueKind.DWord);
                    }
                    output("GPU timeout raised to 8 seconds. Restart for it to take effect.");
                    return true;

                case FixId.CleanTempFiles:
                {
                    long freed = 0; int count = 0;
                    foreach (var dir in new[] { Path.GetTempPath(), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp") })
                    {
                        if (!Directory.Exists(dir)) continue;
                        foreach (var f in Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                        {
                            ct.ThrowIfCancellationRequested();
                            try
                            {
                                var fi = new FileInfo(f);
                                if (fi.LastWriteTime > DateTime.Now.AddDays(-1)) continue;
                                long len = fi.Length;
                                fi.Delete();
                                freed += len; count++;
                            }
                            catch { /* in use */ }
                        }
                    }
                    output($"Deleted {count} temporary files, freed {freed / 1e9:0.00} GB. For more space use Windows Settings › System › Storage › Cleanup recommendations.");
                    return true;
                }
            }
        }
        catch (OperationCanceledException) { output("Cancelled."); }
        catch (Exception ex) { output("Failed: " + ex.Message); }
        return false;
    }

    /// <summary>Schedules chkdsk /spotfix of the system drive at the next restart.</summary>
    public static Task<int> ScheduleSpotFix(Action<string> output) =>
        RunProcess("cmd.exe", "/c echo Y| chkdsk " + (Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\").TrimEnd('\\') + " /spotfix", output, CancellationToken.None, Encoding.UTF8);

    private static async Task<int> RunProcess(string file, string args, Action<string> output, CancellationToken ct, Encoding enc)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = enc, StandardErrorEncoding = enc,
        };
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) output(e.Data.Replace("\0", "").Trim()); };
        p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) output(e.Data.Replace("\0", "").Trim()); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
        return p.ExitCode;
    }
}
