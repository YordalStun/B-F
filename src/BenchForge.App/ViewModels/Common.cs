using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using BenchForge.App.Controls;
using BenchForge.Core.Model;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BenchForge.App.ViewModels;

public abstract partial class PageViewModel : ObservableObject
{
    protected PageViewModel(MainViewModel main, string title, string glyph) { Main = main; Title = title; Glyph = glyph; }
    public MainViewModel Main { get; }
    public string Title { get; }
    public string Glyph { get; }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _showInNav = true;

    partial void OnIsSelectedChanged(bool value)
    {
        if (value && Main.CurrentPage != this) Main.Navigate(this);
    }

    public virtual void OnShown() { }
}

public static class Palette
{
    public static readonly Color Orange = Color.FromRgb(0xFF, 0x7A, 0x3D);
    public static readonly Color Blue = Color.FromRgb(0x3F, 0xA7, 0xFF);
    public static readonly Color Green = Color.FromRgb(0x3C, 0xC4, 0x7A);
    public static readonly Color Purple = Color.FromRgb(0xC7, 0x7D, 0xFF);
    public static readonly Color Yellow = Color.FromRgb(0xF0, 0xB4, 0x29);
    public static readonly Color Pink = Color.FromRgb(0xFF, 0x6B, 0x9E);
    public static readonly Color Teal = Color.FromRgb(0x2E, 0xD3, 0xC6);
    public static readonly Color[] Series = { Orange, Blue, Green, Purple, Yellow, Pink, Teal, Color.FromRgb(0xA0, 0xAE, 0xBC) };

    public static Color For(string keyMetric) => keyMetric.Split('.')[0] switch
    {
        "cpu" => Orange,
        "gpu" => Blue,
        "ram" => Green,
        "disk" => Purple,
        "psu" => Yellow,
        _ => Teal,
    };

    public static SolidColorBrush Brush(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
}

/// <summary>Headline metric tile with a short history.</summary>
public sealed partial class MetricTile : ObservableObject
{
    private readonly List<double> _history = new();
    public MetricTile(string id, string label, string unit)
    {
        Id = id; Label = label; Unit = unit;
        Brush = Palette.Brush(Palette.For(id));
        MaxValue = unit == "%" ? 100 : double.NaN;
    }
    public string Id { get; }
    public string Label { get; }
    public string Unit { get; }
    public SolidColorBrush Brush { get; }
    public double MaxValue { get; }
    public IReadOnlyList<double> History => _history;

    [ObservableProperty] private string _display = "—";
    [ObservableProperty] private double _value;
    [ObservableProperty] private bool _hasValue;
    [ObservableProperty] private int _version;
    [ObservableProperty] private string _range = "";
    private double _min = double.MaxValue, _max = double.MinValue;

    public void Push(double v)
    {
        Value = v;
        HasValue = true;
        _min = Math.Min(_min, v); _max = Math.Max(_max, v);
        Display = Format(v, Unit);
        Range = $"min {Format(_min, "")}  ·  max {Format(_max, "")}";
        _history.Add(v);
        if (_history.Count > 120) _history.RemoveAt(0);
        Version++;
    }

    public static string Format(double v, string unit) => unit switch
    {
        "MHz" when v >= 1000 => $"{v / 1000:0.00} GHz",
        "MB" when v >= 1024 => $"{v / 1024:0.0} GB",
        _ => (Math.Abs(v) >= 100 ? v.ToString("0") : Math.Abs(v) >= 10 ? v.ToString("0.#") : v.ToString("0.##")) + (unit.Length > 0 ? " " + unit : ""),
    };
}

/// <summary>A set of chart lines fed from key metrics.</summary>
public sealed class MetricChart
{
    public MetricChart(string title, string unit, double yMin, double yMax, params string[] keys)
    {
        Title = title; Unit = unit; YMin = yMin; YMax = yMax; Keys = keys;
    }
    public string Title { get; }
    public string Unit { get; }
    public double YMin { get; }
    public double YMax { get; }
    public string[] Keys { get; }
    public ObservableCollection<ChartSeries> Series { get; } = new();
    private readonly Dictionary<string, ChartSeries> _map = new();
    private int _colorIndex;

    public void Push(double t, IReadOnlyDictionary<string, double> values, int maxPoints = 900)
    {
        foreach (var k in Keys)
        {
            if (!values.TryGetValue(k, out var v)) continue;
            if (!_map.TryGetValue(k, out var s))
            {
                var color = Keys.Length <= 2 || _colorIndex == 0 ? Palette.For(k) : Palette.Series[_colorIndex % Palette.Series.Length];
                if (Series.Any(x => x.Color == color)) color = Palette.Series[(_colorIndex + 3) % Palette.Series.Length];
                _colorIndex++;
                s = new ChartSeries(KeyMetrics.Label(k), color, KeyMetrics.Unit(k)) { MaxPoints = maxPoints };
                _map[k] = s;
                Series.Add(s);
            }
            s.Add(t, v);
        }
    }

    public void Clear() { _map.Clear(); Series.Clear(); _colorIndex = 0; }

    public static MetricChart[] Standard() => new[]
    {
        new MetricChart("Temperatures", "°C", 0, double.NaN, KeyMetrics.CpuTemp, KeyMetrics.GpuTemp, KeyMetrics.GpuHotspot, KeyMetrics.GpuMemTemp, KeyMetrics.DiskTemp),
        new MetricChart("Usage", "%", 0, 100, KeyMetrics.CpuLoad, KeyMetrics.GpuLoad, KeyMetrics.RamLoad, KeyMetrics.DiskActivity),
        new MetricChart("Clocks", "MHz", 0, double.NaN, KeyMetrics.CpuClock, KeyMetrics.GpuClock, KeyMetrics.GpuMemClock),
        new MetricChart("Power", "W", 0, double.NaN, KeyMetrics.CpuPower, KeyMetrics.GpuPower),
    };
}

public static class Ui
{
    public static void Post(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d == null) return;
        if (d.CheckAccess()) a(); else d.BeginInvoke(a);
    }
}
