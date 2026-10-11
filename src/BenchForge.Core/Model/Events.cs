namespace BenchForge.Core.Model;

/// <summary>A Windows event log record, platform independent so it can be saved inside results.</summary>
public sealed class EventEntry
{
    public string Log { get; set; } = "";
    public DateTime Time { get; set; }
    public int Id { get; set; }
    public string Provider { get; set; } = "";
    public int Level { get; set; }
    public string Message { get; set; } = "";
    public List<string> Properties { get; set; } = new();
    /// <summary>Named EventData values (e.g. Kernel-Power 41 "BugcheckCode").</summary>
    public Dictionary<string, string> Data { get; set; } = new();

    public string LevelName => Level switch
    {
        1 => "Critical",
        2 => "Error",
        3 => "Warning",
        4 or 0 => "Information",
        5 => "Verbose",
        _ => Level.ToString(),
    };

    public string FirstLine => (Message ?? "").Split('\n', 2)[0].Trim();
}

public enum CrashKind
{
    Unknown,
    /// <summary>Windows blue screen (bugcheck).</summary>
    BlueScreen,
    /// <summary>Hard reset, power loss, freeze → reset or PSU / thermal shutdown (Kernel-Power 41 with no bugcheck).</summary>
    PowerLossOrHang,
    /// <summary>BenchForge itself stopped (crash, killed, closed) without the PC rebooting.</summary>
    AppTerminated,
    /// <summary>The 3D test process died (often a GPU driver crash / TDR).</summary>
    GpuProcessCrash,
}

public enum SignalKind
{
    BugCheck,
    UnexpectedShutdown,
    WheaFatal,
    WheaCorrected,
    GpuDriverReset,
    AppCrash,
    DiskError,
    ServiceFailure,
    ThermalThrottle,
    MemoryDiagnostic,
    Other,
}

/// <summary>An interpreted, crash-relevant fact pulled out of an event or dump.</summary>
public sealed class CrashSignal
{
    public SignalKind Kind { get; set; }
    public DateTime Time { get; set; }
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public uint? BugCheckCode { get; set; }
    /// <summary>Driver / module / app involved (nvlddmkm, ntdll.dll, …).</summary>
    public string? Module { get; set; }
    /// <summary>Hardware part named by WHEA (Processor Core, PCI Express Root Port, Memory …).</summary>
    public string? HardwareSource { get; set; }
    public Component? Component { get; set; }
    public int EventId { get; set; }
    public string Provider { get; set; } = "";
}

public sealed class MinidumpInfo
{
    public string Path { get; set; } = "";
    public DateTime Created { get; set; }
    public long SizeBytes { get; set; }
    public bool Valid { get; set; }
    public string? Error { get; set; }
    public bool Is64Bit { get; set; }
    public uint BugCheckCode { get; set; }
    public ulong[] Parameters { get; set; } = new ulong[4];
    public int Processors { get; set; }
    public string? FaultingModule { get; set; }
    public string? DeepAnalysis { get; set; }
    public List<string> DriversFound { get; set; } = new();

    public string BugCheckHex => $"0x{BugCheckCode:X8}";
}

public sealed class CrashInfo
{
    public CrashKind Kind { get; set; }
    public DateTime LastHeartbeat { get; set; }
    public DateTime DetectedAt { get; set; } = DateTime.Now;
    public bool SystemRebooted { get; set; }
    public string? StepName { get; set; }
    public StepKind? StepKind { get; set; }
    public List<Component> ActiveComponents { get; set; } = new();
    public double RunElapsedSeconds { get; set; }
    public double StepElapsedSeconds { get; set; }
    /// <summary>Key metrics at the very last heartbeat.</summary>
    public Dictionary<string, double> LastMetrics { get; set; } = new();
    /// <summary>Highest key metric values in the final minute before the crash.</summary>
    public Dictionary<string, double> PeakMetrics { get; set; } = new();
    /// <summary>Key metric history for the final minutes (seconds-before-crash → metrics).</summary>
    public List<HeartbeatSample> Tail { get; set; } = new();
    public int ErrorsBeforeCrash { get; set; }
    public List<CrashSignal> Signals { get; set; } = new();
    public List<MinidumpInfo> Dumps { get; set; } = new();
    public string Summary { get; set; } = "";
    public List<string> Explanation { get; set; } = new();
    public Component? SuspectedComponent { get; set; }
}

public sealed class HeartbeatSample
{
    public DateTime Time { get; set; }
    public Dictionary<string, double> Metrics { get; set; } = new();
}
