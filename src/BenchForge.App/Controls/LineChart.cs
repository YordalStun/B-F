using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace BenchForge.App.Controls;

/// <summary>One line on a <see cref="LineChart"/>. X is seconds, Y is the value.</summary>
public sealed class ChartSeries
{
    private readonly object _lock = new();
    private readonly List<Point> _points = new();

    public ChartSeries(string name, Color color, string unit = "") { Name = name; Color = color; Unit = unit; Brush = new SolidColorBrush(color); Brush.Freeze(); }

    public string Name { get; }
    public Color Color { get; }
    public SolidColorBrush Brush { get; }
    public string Unit { get; }
    /// <summary>Keep at most this many points (oldest dropped). 0 = unlimited.</summary>
    public int MaxPoints { get; set; }
    public event Action? Changed;

    public void Add(double x, double y)
    {
        lock (_lock)
        {
            _points.Add(new Point(x, y));
            if (MaxPoints > 0 && _points.Count > MaxPoints) _points.RemoveRange(0, _points.Count - MaxPoints);
        }
        Changed?.Invoke();
    }

    public void Set(IEnumerable<Point> pts)
    {
        lock (_lock) { _points.Clear(); _points.AddRange(pts); }
        Changed?.Invoke();
    }

    public void Clear() { lock (_lock) _points.Clear(); Changed?.Invoke(); }

    public List<Point> Snapshot() { lock (_lock) return _points.ToList(); }
    public double? Last { get { lock (_lock) return _points.Count > 0 ? _points[^1].Y : null; } }
}

/// <summary>Lightweight, fast line chart with grid, legend, time axis and a hover read-out.</summary>
public sealed class LineChart : FrameworkElement
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(nameof(Series), typeof(IEnumerable<ChartSeries>), typeof(LineChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSeriesChanged));
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(LineChart), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(nameof(Unit), typeof(string), typeof(LineChart), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty YMinProperty = DependencyProperty.Register(nameof(YMin), typeof(double), typeof(LineChart), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty YMaxProperty = DependencyProperty.Register(nameof(YMax), typeof(double), typeof(LineChart), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty WindowSecondsProperty = DependencyProperty.Register(nameof(WindowSeconds), typeof(double), typeof(LineChart), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable<ChartSeries>? Series { get => (IEnumerable<ChartSeries>?)GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    /// <summary>Fixed axis minimum (NaN = auto).</summary>
    public double YMin { get => (double)GetValue(YMinProperty); set => SetValue(YMinProperty, value); }
    public double YMax { get => (double)GetValue(YMaxProperty); set => SetValue(YMaxProperty, value); }
    /// <summary>Only show the last N seconds (0 = everything).</summary>
    public double WindowSeconds { get => (double)GetValue(WindowSecondsProperty); set => SetValue(WindowSecondsProperty, value); }

    private Point? _mouse;
    private bool _pending;
    private static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface FaceBold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Brush GridBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x2A, 0x32, 0x3B)));
    private static readonly Brush TextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x93, 0xA0, 0xAD)));
    private static readonly Brush FgBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE6, 0xEA, 0xEE)));
    private static readonly Brush TipBg = Freeze(new SolidColorBrush(Color.FromArgb(0xF0, 0x1E, 0x25, 0x2D)));
    private static readonly Pen GridPen = FreezePen(new Pen(GridBrush, 1));
    private static readonly Pen CursorPen = FreezePen(new Pen(TextBrush, 1) { DashStyle = DashStyles.Dash });

    public LineChart()
    {
        ClipToBounds = true;
        MouseMove += (_, e) => { _mouse = e.GetPosition(this); InvalidateVisual(); };
        MouseLeave += (_, _) => { _mouse = null; InvalidateVisual(); };
    }

    private static T Freeze<T>(T b) where T : Freezable { b.Freeze(); return b; }
    private static Pen FreezePen(Pen p) { p.Freeze(); return p; }

    private static void OnSeriesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var c = (LineChart)d;
        if (e.OldValue is IEnumerable<ChartSeries> old)
        {
            foreach (var s in old) s.Changed -= c.Schedule;
            if (old is INotifyCollectionChanged oc) oc.CollectionChanged -= c.OnCollectionChanged;
        }
        if (e.NewValue is IEnumerable<ChartSeries> nw)
        {
            foreach (var s in nw) s.Changed += c.Schedule;
            if (nw is INotifyCollectionChanged nc) nc.CollectionChanged += c.OnCollectionChanged;
        }
        c.Schedule();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null) foreach (ChartSeries s in e.OldItems) s.Changed -= Schedule;
        if (e.NewItems != null) foreach (ChartSeries s in e.NewItems) s.Changed += Schedule;
        Schedule();
    }

    /// <summary>Coalesces many data updates into one redraw.</summary>
    private void Schedule()
    {
        if (_pending) return;
        _pending = true;
        Dispatcher.BeginInvoke(() => { _pending = false; InvalidateVisual(); }, System.Windows.Threading.DispatcherPriority.Render);
    }

    private FormattedText Text(string s, double size, Brush b, bool bold = false) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, bold ? FaceBold : Face, size, b, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        if (w < 60 || h < 50) return;
        var series = (Series ?? Enumerable.Empty<ChartSeries>()).Select(s => (S: s, P: s.Snapshot())).ToList();

        double top = string.IsNullOrEmpty(Title) ? 22 : 40, left = 46, right = 10, bottom = 22;
        if (!string.IsNullOrEmpty(Title)) dc.DrawText(Text(Title, 13, FgBrush, true), new Point(2, 0));

        // Legend
        double lx = 2, ly = string.IsNullOrEmpty(Title) ? 0 : 19;
        foreach (var (s, p) in series)
        {
            var last = p.Count > 0 ? $" {Fmt(p[^1].Y)}{(string.IsNullOrEmpty(s.Unit) ? "" : " " + s.Unit)}" : "";
            var t = Text(s.Name + last, 11.5, TextBrush);
            if (lx + t.Width + 16 > w) break;
            dc.DrawRoundedRectangle(s.Brush, null, new Rect(lx, ly + 4, 9, 9), 2, 2);
            dc.DrawText(t, new Point(lx + 13, ly));
            lx += t.Width + 26;
        }

        var all = series.SelectMany(x => x.P).ToList();
        double xMax = all.Count > 0 ? all.Max(p => p.X) : 1;
        double xMin = WindowSeconds > 0 ? xMax - WindowSeconds : (all.Count > 0 ? all.Min(p => p.X) : 0);
        if (xMax - xMin < 1) xMax = xMin + 1;
        var visible = all.Where(p => p.X >= xMin).ToList();
        double yMin = !double.IsNaN(YMin) ? YMin : (visible.Count > 0 ? Math.Min(0, visible.Min(p => p.Y)) : 0);
        double yMax = !double.IsNaN(YMax) ? YMax : (visible.Count > 0 ? visible.Max(p => p.Y) : 1);
        if (double.IsNaN(YMax)) yMax = Nice(yMax <= yMin ? yMin + 1 : yMax * 1.05);
        if (yMax <= yMin) yMax = yMin + 1;

        var plot = new Rect(left, top, Math.Max(10, w - left - right), Math.Max(10, h - top - bottom));
        double X(double x) => plot.Left + (x - xMin) / (xMax - xMin) * plot.Width;
        double Y(double y) => plot.Bottom - (y - yMin) / (yMax - yMin) * plot.Height;

        for (int i = 0; i <= 4; i++)
        {
            double v = yMin + (yMax - yMin) * i / 4;
            double y = Math.Round(Y(v)) + 0.5;
            dc.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var t = Text(Fmt(v), 10.5, TextBrush);
            dc.DrawText(t, new Point(plot.Left - t.Width - 6, y - t.Height / 2));
        }
        for (int i = 0; i <= 4; i++)
        {
            double xv = xMin + (xMax - xMin) * i / 4;
            var t = Text(TimeLabel(xv), 10.5, TextBrush);
            double x = X(xv) - t.Width / 2;
            dc.DrawText(t, new Point(Math.Clamp(x, plot.Left - 10, w - t.Width), plot.Bottom + 4));
        }

        if (all.Count == 0)
        {
            var t = Text("Waiting for data…", 12, TextBrush);
            dc.DrawText(t, new Point(plot.Left + (plot.Width - t.Width) / 2, plot.Top + (plot.Height - t.Height) / 2));
            return;
        }

        dc.PushClip(new RectangleGeometry(new Rect(plot.Left, plot.Top - 2, plot.Width, plot.Height + 4)));
        foreach (var (s, p) in series)
        {
            var pts = p.Where(q => q.X >= xMin - 1).ToList();
            if (pts.Count < 2) continue;
            int step = Math.Max(1, pts.Count / (int)Math.Max(50, plot.Width));
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(X(pts[0].X), Y(pts[0].Y)), false, false);
                var line = new List<Point>(pts.Count / step + 1);
                for (int i = step; i < pts.Count; i += step) line.Add(new Point(X(pts[i].X), Y(pts[i].Y)));
                line.Add(new Point(X(pts[^1].X), Y(pts[^1].Y)));
                ctx.PolyLineTo(line, true, true);
            }
            geo.Freeze();
            var pen = new Pen(s.Brush, 1.7) { LineJoin = PenLineJoin.Round };
            pen.Freeze();
            dc.DrawGeometry(null, pen, geo);
            var end = new Point(X(pts[^1].X), Y(pts[^1].Y));
            dc.DrawEllipse(s.Brush, null, end, 3, 3);
        }
        dc.Pop();

        // Hover read-out
        if (_mouse is Point m && plot.Contains(m))
        {
            double xv = xMin + (m.X - plot.Left) / plot.Width * (xMax - xMin);
            dc.DrawLine(CursorPen, new Point(m.X, plot.Top), new Point(m.X, plot.Bottom));
            var lines = new List<(Brush B, string T)> { (FgBrush, TimeLabel(xv)) };
            foreach (var (s, p) in series)
            {
                if (p.Count == 0) continue;
                var near = p.MinBy(q => Math.Abs(q.X - xv));
                lines.Add((s.Brush, $"{s.Name}: {Fmt(near.Y)} {s.Unit}"));
            }
            var texts = lines.Select(l => (l.B, F: Text(l.T, 11.5, l.B == FgBrush ? FgBrush : FgBrush))).ToList();
            double tw = texts.Max(t => t.F.Width) + 26, th = texts.Sum(t => t.F.Height) + 10;
            double tx = m.X + 12 + tw > w ? m.X - tw - 12 : m.X + 12, ty = Math.Clamp(m.Y - th / 2, 0, Math.Max(0, h - th));
            dc.DrawRoundedRectangle(TipBg, GridPen, new Rect(tx, ty, tw, th), 6, 6);
            double yy = ty + 5;
            foreach (var (b, f) in texts)
            {
                if (b != FgBrush) dc.DrawRoundedRectangle(b, null, new Rect(tx + 8, yy + 4, 8, 8), 2, 2);
                dc.DrawText(f, new Point(tx + (b != FgBrush ? 20 : 8), yy));
                yy += f.Height;
            }
        }
    }

    private static string Fmt(double v) => Math.Abs(v) >= 1000 ? v.ToString("N0") : Math.Abs(v) >= 100 ? v.ToString("0") : v.ToString("0.#");

    private static string TimeLabel(double s)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, s));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    private static double Nice(double v)
    {
        if (v <= 0) return 1;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(v)));
        foreach (var k in new[] { 1, 1.2, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10 }) if (k * mag >= v) return k * mag;
        return 10 * mag;
    }
}

/// <summary>Tiny trend line for tiles.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(nameof(Values), typeof(IReadOnlyList<double>), typeof(Sparkline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty VersionProperty = DependencyProperty.Register(nameof(Version), typeof(int), typeof(Sparkline), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(Sparkline), new FrameworkPropertyMetadata(Brushes.Orange, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MaxValueProperty = DependencyProperty.Register(nameof(MaxValue), typeof(double), typeof(Sparkline), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values { get => (IReadOnlyList<double>?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    /// <summary>Bump to redraw after the list changed.</summary>
    public int Version { get => (int)GetValue(VersionProperty); set => SetValue(VersionProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double MaxValue { get => (double)GetValue(MaxValueProperty); set => SetValue(MaxValueProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var v = Values?.ToList();
        if (v == null || v.Count < 2 || ActualWidth < 4) return;
        double min = Math.Min(0, v.Min()), max = double.IsNaN(MaxValue) ? Math.Max(v.Max(), min + 1) : MaxValue;
        double w = ActualWidth, h = ActualHeight;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(0, h), true, true);
            for (int i = 0; i < v.Count; i++)
                ctx.LineTo(new Point(i * w / (v.Count - 1), h - (v[i] - min) / (max - min) * (h - 2) - 1), true, true);
            ctx.LineTo(new Point(w, h), false, false);
        }
        geo.Freeze();
        var fill = Stroke.Clone(); fill.Opacity = 0.15;
        dc.DrawGeometry(fill, new Pen(Stroke, 1.4), geo);
    }
}
