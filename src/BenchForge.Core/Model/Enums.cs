namespace BenchForge.Core.Model;

public enum Component { Cpu, Memory, Storage, Gpu }

public enum StepKind { Benchmark, Stress }

public enum RunStatus { Running, Completed, CompletedWithErrors, Aborted, Cancelled, Crashed }

public enum HwType { Cpu, Gpu, Memory, Storage, Motherboard, Network, Psu, Cooler, Test, Other }

public enum SensorKind { Temperature, Load, Clock, Power, Fan, Voltage, Current, Data, Throughput, Frequency, Level, Other }

public enum Severity { Info, Warning, Critical }

public enum CpuStressMode
{
    /// <summary>Rotates through every CPU workload (best for finding instability).</summary>
    Mixed,
    /// <summary>Vector FMA matrix maths — maximum power draw and heat.</summary>
    MaxHeat,
    /// <summary>Integer / branchy workloads (sieve, sort, compression).</summary>
    Integer,
    /// <summary>Floating point physics + vector maths.</summary>
    FloatingPoint,
}

public static class ComponentNames
{
    public static string Display(this Component c) => c switch
    {
        Component.Cpu => "CPU",
        Component.Memory => "RAM",
        Component.Storage => "Storage",
        Component.Gpu => "GPU",
        _ => c.ToString(),
    };
}
