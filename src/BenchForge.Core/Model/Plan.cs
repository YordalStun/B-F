namespace BenchForge.Core.Model;

public sealed class TestStep
{
    public string Name { get; set; } = "";
    public StepKind Kind { get; set; }
    public List<Component> Components { get; set; } = new();
    public double DurationSeconds { get; set; }
    /// <summary>Special test flavour, e.g. <see cref="StepVariants.DriveHealth"/>.</summary>
    public string? Variant { get; set; }
    /// <summary>Drive/folder for storage steps; overrides <see cref="TestSettings.StorageTarget"/>.</summary>
    public string? Target { get; set; }

    public TestStep() { }
    public TestStep(string name, StepKind kind, double seconds, params Component[] components)
    {
        Name = name; Kind = kind; DurationSeconds = seconds; Components = components.ToList();
    }

    public override string ToString() => $"{Name} ({TimeFormat.Short(TimeSpan.FromSeconds(DurationSeconds))})";
}

public enum LaptopMode { Auto, On, Off }

public static class StepVariants
{
    /// <summary>Storage: surface read scan + sustained-write (SLC cache) test.</summary>
    public const string DriveHealth = "drive-health";
}

public sealed class TestSettings
{
    /// <summary>Auto = laptop rules when a laptop is detected.</summary>
    public LaptopMode LaptopMode { get; set; } = LaptopMode.Auto;
    /// <summary>Resolved at start: judge this run with laptop rules (running at the thermal limit is normal).</summary>
    public bool LaptopRules { get; set; }

    public CpuStressMode CpuStressMode { get; set; } = CpuStressMode.Mixed;
    /// <summary>0 = all logical processors.</summary>
    public int CpuThreads { get; set; }
    /// <summary>Percentage of currently free RAM used by the RAM stress/error test.</summary>
    public double MemoryPercent { get; set; } = 70;
    /// <summary>Folder used for the storage test file (drive root or folder).</summary>
    public string StorageTarget { get; set; } = "";
    public int StorageFileMB { get; set; } = 1024;
    public int GpuWidth { get; set; } = 1920;
    public int GpuHeight { get; set; } = 1080;
    public bool GpuFullscreen { get; set; }
    public double CpuTempLimit { get; set; } = 95;
    public double GpuTempLimit { get; set; } = 90;
    /// <summary>Stop the whole run as soon as any computation/memory error is found.</summary>
    public bool StopOnError { get; set; }
    /// <summary>Re-open BenchForge at next logon if the PC crashes during the run.</summary>
    public bool ReopenAfterCrash { get; set; } = true;
}

public sealed class TestPlan
{
    public string Name { get; set; } = "Custom";
    public string Description { get; set; } = "";
    public List<TestStep> Steps { get; set; } = new();
    public int Loops { get; set; } = 1;
    public TestSettings Settings { get; set; } = new();

    public double TotalSeconds => Steps.Sum(s => s.DurationSeconds) * Math.Max(1, Loops);

    /// <summary>The flattened list of steps actually executed (loops expanded).</summary>
    public List<TestStep> Expand()
    {
        var list = new List<TestStep>();
        int loops = Math.Max(1, Loops);
        for (int l = 0; l < loops; l++)
            foreach (var s in Steps)
                list.Add(new TestStep(loops > 1 ? $"{s.Name} (loop {l + 1}/{loops})" : s.Name, s.Kind, s.DurationSeconds, s.Components.ToArray()) { Variant = s.Variant, Target = s.Target });
        return list;
    }
}

public static class TimeFormat
{
    public static string Short(TimeSpan t)
    {
        if (t.TotalSeconds < 0) t = TimeSpan.Zero;
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes:00}m";
        if (t.TotalMinutes >= 1) return $"{t.Minutes}m {t.Seconds:00}s";
        return $"{t.Seconds}s";
    }

    public static string Long(TimeSpan t)
    {
        if (t.TotalSeconds < 0) t = TimeSpan.Zero;
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
    }
}
