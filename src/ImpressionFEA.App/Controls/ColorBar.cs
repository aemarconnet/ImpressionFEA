using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ImpressionFEA.App.Controls;

/// <summary>
/// Vertical banded legend for the contour palette with iso-clipping: drag the top or bottom handle to set the visible
/// range (elements outside it are hidden by the view); double-click resets to the full range.
/// </summary>
public sealed class ColorBar : FrameworkElement
{
    public static readonly DependencyProperty RangeMinProperty = DependencyProperty.Register(nameof(RangeMin), typeof(double), typeof(ColorBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RangeMaxProperty = DependencyProperty.Register(nameof(RangeMax), typeof(double), typeof(ColorBar),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ClipLowProperty = DependencyProperty.Register(nameof(ClipLow), typeof(double), typeof(ColorBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty ClipHighProperty = DependencyProperty.Register(nameof(ClipHigh), typeof(double), typeof(ColorBar),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    private static readonly Typeface Mono = new(new FontFamily("Cascadia Mono, Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Pen Outline = Freeze(new Pen(Brushes.Black, 1));
    private static readonly Brush Ink = Brushes.Black;
    private static readonly Brush Backdrop = Freeze(new SolidColorBrush(Color.FromArgb(0xD8, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush Hidden = Freeze(new SolidColorBrush(Color.FromArgb(0xC8, 0xF4, 0xF5, 0xF7)));
    private static readonly Brush Handle = Freeze(new SolidColorBrush(Color.FromRgb(0x0B, 0x5C, 0xAD)));

    private int _dragging; // 0 none, 1 low, 2 high

    public ColorBar()
    {
        Cursor = Cursors.SizeNS;
        ToolTip = "Iso-clip: drag the top or bottom handle to hide elements outside a range · double-click to reset";
    }

    public double RangeMin { get => (double)GetValue(RangeMinProperty); set => SetValue(RangeMinProperty, value); }

    public double RangeMax { get => (double)GetValue(RangeMaxProperty); set => SetValue(RangeMaxProperty, value); }

    /// <summary>Lower end of the visible range as a fraction of the colour range (0 = no clipping).</summary>
    public double ClipLow { get => (double)GetValue(ClipLowProperty); set => SetValue(ClipLowProperty, value); }

    /// <summary>Upper end of the visible range as a fraction of the colour range (1 = no clipping).</summary>
    public double ClipHigh { get => (double)GetValue(ClipHighProperty); set => SetValue(ClipHighProperty, value); }

    /// <summary>Label text for a value (default: adaptive number). Call InvalidateVisual after changing it.</summary>
    public Func<double, string>? Formatter { get; set; }

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    private Rect BarRect => new(6, 8, 16, Math.Max(40, ActualHeight - 16));

    protected override Size MeasureOverride(Size availableSize) => new(80, Math.Min(330, double.IsInfinity(availableSize.Height) ? 330 : availableSize.Height));

    protected override void OnRender(DrawingContext dc)
    {
        var r = BarRect;
        dc.DrawRectangle(Backdrop, null, new Rect(0, 0, ActualWidth, ActualHeight));
        int bands = ContourPalette.Bands;
        double bandH = r.Height / bands;
        for (int i = 0; i < bands; i++)
        {
            var brush = new SolidColorBrush(ContourPalette.ColorOf(i));
            brush.Freeze();
            dc.DrawRectangle(brush, null, new Rect(r.Left, r.Bottom - (i + 1) * bandH, r.Width, bandH + 0.5));
        }

        DrawClip(dc, r, ClipLow, ClipHigh);
        dc.DrawRectangle(null, Outline, r);
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (int i = 0; i <= bands; i += 2)
        {
            double v = RangeMin + (RangeMax - RangeMin) * i / bands;
            double y = r.Bottom - i * bandH;
            dc.DrawLine(Outline, new Point(r.Right, y), new Point(r.Right + 4, y));
            var ft = new FormattedText(Formatter?.Invoke(v) ?? ContourPalette.Format(v), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, 10.5, Ink, pixelsPerDip);
            dc.DrawText(ft, new Point(r.Right + 6, y - 7));
        }
    }

    /// <summary>Greys out the clipped ends of a bar and draws the two drag handles (shared with the 2D view's legend).</summary>
    public static void DrawClip(DrawingContext dc, Rect r, double low, double high)
    {
        double yLow = r.Bottom - low * r.Height, yHigh = r.Bottom - high * r.Height;
        if (low > 0) dc.DrawRectangle(Hidden, null, new Rect(r.Left, yLow, r.Width, r.Bottom - yLow));
        if (high < 1) dc.DrawRectangle(Hidden, null, new Rect(r.Left, r.Top, r.Width, yHigh - r.Top));
        foreach (double y in new[] { yLow, yHigh })
        {
            var tri = new StreamGeometry();
            using (var g = tri.Open())
            {
                g.BeginFigure(new Point(r.Left - 1, y), true, true);
                g.LineTo(new Point(r.Left - 6, y - 4), true, false);
                g.LineTo(new Point(r.Left - 6, y + 4), true, false);
            }

            tri.Freeze();
            dc.DrawGeometry(Handle, null, tri);
            dc.DrawLine(new Pen(Handle, 2), new Point(r.Left, y), new Point(r.Right, y));
        }
    }

    /// <summary>Which handle a press at <paramref name="y"/> grabs (the nearer one), and the clip fraction at y.</summary>
    public static (int Handle, double Fraction) Hit(Rect r, double y, double low, double high)
    {
        double f = Math.Clamp((r.Bottom - y) / r.Height, 0, 1);
        return (Math.Abs(f - low) <= Math.Abs(f - high) ? 1 : 2, f);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        e.Handled = true; // do not orbit the view
        if (e.ClickCount == 2)
        {
            ClipLow = 0;
            ClipHigh = 1;
            return;
        }

        var (h, f) = Hit(BarRect, e.GetPosition(this).Y, ClipLow, ClipHigh);
        _dragging = h;
        Apply(f);
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (_dragging == 0) return;
        Apply(Math.Clamp((BarRect.Bottom - e.GetPosition(this).Y) / BarRect.Height, 0, 1));
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _dragging = 0;
        ReleaseMouseCapture();
        if (e is not null) e.Handled = true;
    }

    private void Apply(double f)
    {
        // Snap to band boundaries so the clip matches what the colours show.
        double snapped = Math.Round(f * ContourPalette.Bands) / ContourPalette.Bands;
        if (_dragging == 1) ClipLow = Math.Min(snapped, ClipHigh - 1.0 / ContourPalette.Bands);
        else ClipHigh = Math.Max(snapped, ClipLow + 1.0 / ContourPalette.Bands);
    }
}
