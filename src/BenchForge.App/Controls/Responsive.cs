using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BenchForge.App.Controls;

/// <summary>
/// Put directly inside a ScrollViewer: makes its content exactly as big as the visible area, but never smaller than
/// MinViewportWidth/Height — so pages fill large screens and scroll (instead of squashing) on small laptop screens.
/// </summary>
public sealed class ViewportPanel : Panel
{
    public static readonly DependencyProperty MinViewportHeightProperty = DependencyProperty.Register(nameof(MinViewportHeight), typeof(double), typeof(ViewportPanel), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty MinViewportWidthProperty = DependencyProperty.Register(nameof(MinViewportWidth), typeof(double), typeof(ViewportPanel), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public double MinViewportHeight { get => (double)GetValue(MinViewportHeightProperty); set => SetValue(MinViewportHeightProperty, value); }
    public double MinViewportWidth { get => (double)GetValue(MinViewportWidthProperty); set => SetValue(MinViewportWidthProperty, value); }

    private ScrollViewer? _sv;

    public ViewportPanel()
    {
        Loaded += (_, _) =>
        {
            DependencyObject? p = VisualTreeHelper.GetParent(this);
            while (p != null && p is not ScrollViewer) p = VisualTreeHelper.GetParent(p);
            if (_sv != null) _sv.SizeChanged -= OnViewportChanged;
            _sv = p as ScrollViewer;
            if (_sv != null) _sv.SizeChanged += OnViewportChanged;
            InvalidateMeasure();
        };
    }

    private void OnViewportChanged(object sender, SizeChangedEventArgs e) => InvalidateMeasure();

    private Size Target(Size available)
    {
        double vw = _sv != null && _sv.ViewportWidth > 0 ? _sv.ViewportWidth : available.Width;
        double vh = _sv != null && _sv.ViewportHeight > 0 ? _sv.ViewportHeight : available.Height;
        if (double.IsInfinity(vw)) vw = MinViewportWidth > 0 ? MinViewportWidth : 1000;
        if (double.IsInfinity(vh)) vh = MinViewportHeight > 0 ? MinViewportHeight : 700;
        vw -= Margin.Left + Margin.Right;
        vh -= Margin.Top + Margin.Bottom;
        return new Size(Math.Max(MinViewportWidth, vw), Math.Max(MinViewportHeight, vh));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var t = Target(availableSize);
        foreach (UIElement c in InternalChildren) c.Measure(t);
        return t;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (UIElement c in InternalChildren) c.Arrange(new Rect(finalSize));
        return finalSize;
    }
}

/// <summary>
/// Two children side by side when there is room (one of them with a fixed width), stacked on top of each other on
/// narrow windows. When stacked inside a fixed height, the height is shared using NarrowFirstRatio.
/// </summary>
public sealed class AdaptiveSplit : Panel
{
    public static readonly DependencyProperty BreakpointProperty = DependencyProperty.Register(nameof(Breakpoint), typeof(double), typeof(AdaptiveSplit), new FrameworkPropertyMetadata(900.0, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty FixedWidthProperty = DependencyProperty.Register(nameof(FixedWidth), typeof(double), typeof(AdaptiveSplit), new FrameworkPropertyMetadata(360.0, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty FixedChildProperty = DependencyProperty.Register(nameof(FixedChild), typeof(int), typeof(AdaptiveSplit), new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(nameof(Gap), typeof(double), typeof(AdaptiveSplit), new FrameworkPropertyMetadata(14.0, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty NarrowFirstRatioProperty = DependencyProperty.Register(nameof(NarrowFirstRatio), typeof(double), typeof(AdaptiveSplit), new FrameworkPropertyMetadata(0.6, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Below this width the two children are stacked.</summary>
    public double Breakpoint { get => (double)GetValue(BreakpointProperty); set => SetValue(BreakpointProperty, value); }
    /// <summary>Width of the fixed child when side by side; 0 = both children get half.</summary>
    public double FixedWidth { get => (double)GetValue(FixedWidthProperty); set => SetValue(FixedWidthProperty, value); }
    /// <summary>Which child (0 or 1) has the fixed width.</summary>
    public int FixedChild { get => (int)GetValue(FixedChildProperty); set => SetValue(FixedChildProperty, value); }
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }
    public double NarrowFirstRatio { get => (double)GetValue(NarrowFirstRatioProperty); set => SetValue(NarrowFirstRatioProperty, value); }

    private bool Wide(double width) => double.IsInfinity(width) || width >= Breakpoint;

    private (double W0, double W1) Widths(double total)
    {
        double avail = Math.Max(0, total - Gap);
        if (FixedWidth <= 0) return (avail / 2, avail / 2);
        double fixedW = Math.Min(FixedWidth, avail * 0.6);
        return FixedChild == 0 ? (fixedW, avail - fixedW) : (avail - fixedW, fixedW);
    }

    private (double H0, double H1) Heights(double total)
    {
        double avail = Math.Max(0, total - Gap);
        return (avail * NarrowFirstRatio, avail * (1 - NarrowFirstRatio));
    }

    protected override Size MeasureOverride(Size available)
    {
        var kids = InternalChildren.Cast<UIElement>().Take(2).ToList();
        if (kids.Count < 2) { foreach (var k in kids) k.Measure(available); return kids.Count == 1 ? kids[0].DesiredSize : new Size(); }
        double width = double.IsInfinity(available.Width) ? Breakpoint : available.Width;
        if (Wide(available.Width))
        {
            var (w0, w1) = Widths(width);
            kids[0].Measure(new Size(w0, available.Height));
            kids[1].Measure(new Size(w1, available.Height));
            return new Size(width, double.IsInfinity(available.Height) ? Math.Max(kids[0].DesiredSize.Height, kids[1].DesiredSize.Height) : available.Height);
        }
        if (double.IsInfinity(available.Height))
        {
            kids[0].Measure(new Size(width, double.PositiveInfinity));
            kids[1].Measure(new Size(width, double.PositiveInfinity));
            return new Size(width, kids[0].DesiredSize.Height + Gap + kids[1].DesiredSize.Height);
        }
        var (h0, h1) = Heights(available.Height);
        kids[0].Measure(new Size(width, h0));
        kids[1].Measure(new Size(width, h1));
        return new Size(width, available.Height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var kids = InternalChildren.Cast<UIElement>().Take(2).ToList();
        if (kids.Count < 2) { foreach (var k in kids) k.Arrange(new Rect(final)); return final; }
        if (Wide(final.Width))
        {
            var (w0, w1) = Widths(final.Width);
            kids[0].Arrange(new Rect(0, 0, w0, final.Height));
            kids[1].Arrange(new Rect(w0 + Gap, 0, w1, final.Height));
        }
        else
        {
            double h0, h1;
            if (DesiredSize.Height > 0 && Math.Abs(kids[0].DesiredSize.Height + Gap + kids[1].DesiredSize.Height - final.Height) < 1)
            { h0 = kids[0].DesiredSize.Height; h1 = kids[1].DesiredSize.Height; }
            else (h0, h1) = Heights(final.Height);
            kids[0].Arrange(new Rect(0, 0, final.Width, h0));
            kids[1].Arrange(new Rect(0, h0 + Gap, final.Width, h1));
        }
        // Hide anything beyond the first two children.
        foreach (var extra in InternalChildren.Cast<UIElement>().Skip(2)) extra.Arrange(new Rect());
        return final;
    }
}

/// <summary>Grid that picks how many columns fit (each at least MinItemWidth), so cards and charts reflow on small screens.</summary>
public sealed class AdaptiveColumns : Panel
{
    public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(nameof(MinItemWidth), typeof(double), typeof(AdaptiveColumns), new FrameworkPropertyMetadata(320.0, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(nameof(MaxColumns), typeof(int), typeof(AdaptiveColumns), new FrameworkPropertyMetadata(2, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public double MinItemWidth { get => (double)GetValue(MinItemWidthProperty); set => SetValue(MinItemWidthProperty, value); }
    public int MaxColumns { get => (int)GetValue(MaxColumnsProperty); set => SetValue(MaxColumnsProperty, value); }

    private int Columns(double width) =>
        double.IsInfinity(width) ? MaxColumns : Math.Clamp((int)Math.Floor(width / Math.Max(1, MinItemWidth)), 1, Math.Max(1, MaxColumns));

    protected override Size MeasureOverride(Size available)
    {
        int cols = Columns(available.Width);
        double colW = double.IsInfinity(available.Width) ? MinItemWidth : available.Width / cols;
        double total = 0, rowH = 0;
        int i = 0;
        foreach (UIElement c in InternalChildren)
        {
            c.Measure(new Size(colW, double.PositiveInfinity));
            rowH = Math.Max(rowH, c.DesiredSize.Height);
            if (++i % cols == 0) { total += rowH; rowH = 0; }
        }
        total += rowH;
        return new Size(double.IsInfinity(available.Width) ? colW * cols : available.Width, total);
    }

    protected override Size ArrangeOverride(Size final)
    {
        int cols = Columns(final.Width);
        double colW = final.Width / cols;
        var kids = InternalChildren.Cast<UIElement>().ToList();
        double y = 0;
        for (int r = 0; r * cols < kids.Count; r++)
        {
            var row = kids.Skip(r * cols).Take(cols).ToList();
            double h = row.Max(k => k.DesiredSize.Height);
            for (int c = 0; c < row.Count; c++) row[c].Arrange(new Rect(c * colW, y, colW, h));
            y += h;
        }
        return final;
    }
}
