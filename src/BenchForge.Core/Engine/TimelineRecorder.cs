using BenchForge.Core.Model;

namespace BenchForge.Core.Engine;

/// <summary>
/// Records sensor snapshots into a compact timeline. Exact min/avg/max are tracked for every sample,
/// while the stored timeline is thinned for very long runs (keeps files small for multi-hour burn tests).
/// </summary>
public sealed class TimelineRecorder
{
    private const int MaxChannels = 600;
    private readonly SensorTimeline _timeline = new();
    private readonly Dictionary<string, int> _index = new();
    private readonly Dictionary<string, Acc> _stats = new();
    private readonly Dictionary<string, Acc> _keyStats = new();
    private readonly DateTime _start;
    private readonly int _keepEvery;
    private long _sampleCount;
    private readonly object _lock = new();

    private sealed class Acc
    {
        public double Min = double.MaxValue, Max = double.MinValue, Sum, Last;
        public long Count;
        public void Add(double v) { if (v < Min) Min = v; if (v > Max) Max = v; Sum += v; Count++; Last = v; }
    }

    public TimelineRecorder(DateTime start, double plannedSeconds, int targetSamples = 3600)
    {
        _start = start;
        _keepEvery = Math.Max(1, (int)Math.Ceiling(plannedSeconds / targetSamples));
        _timeline.IntervalSeconds = _keepEvery;
    }

    public void Add(SensorSnapshot snap, IReadOnlyDictionary<string, double> keyMetrics)
    {
        lock (_lock)
        {
            foreach (var r in snap.Readings)
            {
                if (!_index.ContainsKey(r.Id) && _index.Count < MaxChannels)
                {
                    _index[r.Id] = _timeline.Channels.Count;
                    _timeline.Channels.Add(new SensorChannel { Id = r.Id, Hardware = r.Hardware, HwType = r.HwType, Kind = r.Kind, Name = r.Name, Unit = r.Unit });
                }
                if (!_stats.TryGetValue(r.Id, out var acc)) _stats[r.Id] = acc = new Acc();
                acc.Add(r.Value);
            }
            foreach (var (k, v) in keyMetrics)
            {
                if (!_keyStats.TryGetValue(k, out var acc)) _keyStats[k] = acc = new Acc();
                acc.Add(v);
            }

            if (_sampleCount++ % _keepEvery != 0) return;
            var values = new float?[_timeline.Channels.Count];
            foreach (var r in snap.Readings)
                if (_index.TryGetValue(r.Id, out int i))
                    values[i] = (float)Math.Round(r.Value, 2);
            _timeline.Samples.Add(new TimelineSample { T = Math.Round((snap.Time - _start).TotalSeconds, 1), V = values });
        }
    }

    public void Fill(RunResult result)
    {
        lock (_lock)
        {
            result.Timeline = _timeline;
            result.SensorStats = _timeline.Channels.Where(c => _stats.ContainsKey(c.Id)).Select(c =>
            {
                var a = _stats[c.Id];
                return new ChannelStats
                {
                    Id = c.Id, Hardware = c.Hardware, Name = c.Name, HwType = c.HwType, Kind = c.Kind, Unit = c.Unit,
                    Min = Math.Round(a.Min, 2), Max = Math.Round(a.Max, 2), Avg = Math.Round(a.Sum / a.Count, 2), Last = Math.Round(a.Last, 2), Count = a.Count,
                };
            }).ToList();
            result.KeyStats = _keyStats.ToDictionary(kv => kv.Key, kv => new ChannelStats
            {
                Id = kv.Key, Name = KeyMetrics.Label(kv.Key), Unit = KeyMetrics.Unit(kv.Key),
                Min = Math.Round(kv.Value.Min, 2), Max = Math.Round(kv.Value.Max, 2), Avg = Math.Round(kv.Value.Sum / kv.Value.Count, 2), Last = Math.Round(kv.Value.Last, 2), Count = kv.Value.Count,
            });
        }
    }
}
