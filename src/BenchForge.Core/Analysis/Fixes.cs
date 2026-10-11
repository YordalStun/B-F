namespace BenchForge.Core.Analysis;

/// <summary>Software-side problems BenchForge can fix itself (always after asking).</summary>
public enum FixId
{
    RepairSystemFiles,
    ScanDisk,
    MemoryDiagnostic,
    PowerPlanBalanced,
    DisableFastStartup,
    EnableCrashDumps,
    IncreaseTdrDelay,
    CleanTempFiles,
}

public sealed record FixInfo(FixId Id, string Title, string WhatItDoes, bool NeedsRestart, string HowToUndo);

public static class Fixes
{
    public static readonly IReadOnlyDictionary<FixId, FixInfo> All = new Dictionary<FixId, FixInfo>
    {
        [FixId.RepairSystemFiles] = new(FixId.RepairSystemFiles, "Repair Windows system files",
            "Runs DISM /RestoreHealth then SFC /scannow to find and replace corrupted Windows files. Takes 10–30 minutes; needs internet for DISM.", false, "Nothing to undo — it only restores original Windows files."),
        [FixId.ScanDisk] = new(FixId.ScanDisk, "Check the system drive for errors",
            "Runs 'chkdsk C: /scan' while Windows keeps running. If it finds problems, it offers to repair them at the next restart.", false, "Nothing to undo."),
        [FixId.MemoryDiagnostic] = new(FixId.MemoryDiagnostic, "Run Windows Memory Diagnostic",
            "Opens Windows' own RAM test, which runs before Windows starts. Choose 'Restart now' or 'next time'. The result appears in BenchForge › Event Viewer › Windows Memory Diagnostic.", true, "Nothing to undo."),
        [FixId.PowerPlanBalanced] = new(FixId.PowerPlanBalanced, "Switch power plan to Balanced",
            "Leaves 'Power saver', which caps CPU speed. Balanced still saves power when idle.", false, "Control Panel › Power Options › choose another plan."),
        [FixId.DisableFastStartup] = new(FixId.DisableFastStartup, "Turn off Fast Startup",
            "Fast Startup keeps drivers in a half-sleeping state between shutdowns and is a common cause of power-state blue screens (0x9F) and odd behaviour after shutdown.", false, "Control Panel › Power Options › Choose what the power buttons do › Turn on fast startup."),
        [FixId.EnableCrashDumps] = new(FixId.EnableCrashDumps, "Make Windows save crash dumps",
            "Sets Windows to write a small memory dump on every blue screen (C:\\Windows\\Minidump), so BenchForge can read the stop code and cause next time.", true, "System Properties › Advanced › Startup and Recovery › Write debugging information."),
        [FixId.IncreaseTdrDelay] = new(FixId.IncreaseTdrDelay, "Give the GPU driver more time before resetting (TDR delay 8 s)",
            "Windows resets the graphics driver if the GPU doesn't respond within 2 seconds. Raising this to 8 seconds stops false resets on heavy loads. It does NOT fix a truly unstable GPU.", true, "Delete the TdrDelay value under HKLM\\SYSTEM\\CurrentControlSet\\Control\\GraphicsDrivers."),
        [FixId.CleanTempFiles] = new(FixId.CleanTempFiles, "Clean temporary files",
            "Deletes temporary files older than a day from your Temp folder and Windows\\Temp to free disk space.", false, "Not needed — only temporary files are removed."),
    };

    public static FixInfo Get(FixId id) => All[id];
}
