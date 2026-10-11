namespace BenchForge.Core.Model;

/// <summary>One reading from one sensor at one point in time.</summary>
public sealed record SensorReading(string Id, string Hardware, HwType HwType, SensorKind Kind, string Name, double Value, string Unit);

public sealed class SensorSnapshot
{
    public DateTime Time { get; init; } = DateTime.Now;
    public List<SensorReading> Readings { get; init; } = new();

    public SensorReading? Find(string id) => Readings.FirstOrDefault(r => r.Id == id);
}

/// <summary>Reads hardware sensors. Implemented per platform (LibreHardwareMonitor on Windows).</summary>
public interface ISensorProvider : IDisposable
{
    /// <summary>Human readable warning when sensors are limited (e.g. not admin, driver missing).</summary>
    string? Warning { get; }
    SensorSnapshot Read();
}

public static class SensorUnits
{
    public static string For(SensorKind kind) => kind switch
    {
        SensorKind.Temperature => "°C",
        SensorKind.Load => "%",
        SensorKind.Level => "%",
        SensorKind.Clock => "MHz",
        SensorKind.Frequency => "Hz",
        SensorKind.Power => "W",
        SensorKind.Fan => "RPM",
        SensorKind.Voltage => "V",
        SensorKind.Current => "A",
        SensorKind.Data => "GB",
        SensorKind.Throughput => "MB/s",
        _ => "",
    };
}

/// <summary>Column definition for a recorded sensor.</summary>
public sealed class SensorChannel
{
    public string Id { get; set; } = "";
    public string Hardware { get; set; } = "";
    public HwType HwType { get; set; }
    public SensorKind Kind { get; set; }
    public string Name { get; set; } = "";
    public string Unit { get; set; } = "";

    public string DisplayName => $"{Hardware} · {Name}";
}

public sealed class TimelineSample
{
    /// <summary>Seconds since the run started.</summary>
    public double T { get; set; }
    /// <summary>Values in the same order as <see cref="SensorTimeline.Channels"/>; null = not available.</summary>
    public float?[] V { get; set; } = Array.Empty<float?>();
}

public sealed class SensorTimeline
{
    public List<SensorChannel> Channels { get; set; } = new();
    public List<TimelineSample> Samples { get; set; } = new();
    public double IntervalSeconds { get; set; } = 1;

    public int IndexOf(string channelId) => Channels.FindIndex(c => c.Id == channelId);

    public IEnumerable<(double T, double V)> Series(string channelId)
    {
        int i = IndexOf(channelId);
        if (i < 0) yield break;
        foreach (var s in Samples)
            if (i < s.V.Length && s.V[i] is float f)
                yield return (s.T, f);
    }
}

public sealed class ChannelStats
{
    public string Id { get; set; } = "";
    public string Hardware { get; set; } = "";
    public string Name { get; set; } = "";
    public HwType HwType { get; set; }
    public SensorKind Kind { get; set; }
    public string Unit { get; set; } = "";
    public double Min { get; set; }
    public double Max { get; set; }
    public double Avg { get; set; }
    public double Last { get; set; }
    public long Count { get; set; }
}
