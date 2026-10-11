using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace ImpressionFEA.App.Controls;

/// <summary>Vertical banded legend for the contour palette.</summary>
public sealed class ColorBar : FrameworkElement
{
    public static readonly DependencyProperty RangeMinProperty = DependencyProperty.Register(nameof(RangeMin), typeof(double), typeof(ColorBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RangeMaxProperty = DependencyProperty.Register(nameof(RangeMax), typeof(double), typeof(ColorBar),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Typeface Mono = new(new FontFamily("Cascadia Mono, Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Pen Outline = Freeze(new Pen(Brushes.Black, 1));
    private static readonly Brush Ink = Brushes.Black;
    private static readonly Brush Backdrop = Freeze(new SolidColorBrush(Color.FromArgb(0xD8, 0xFF, 0xFF, 0xFF)));

    public double RangeMin { get => (double)GetValue(RangeMinProperty); set => SetValue(RangeMinProperty, value); }

    public double RangeMax { get => (double)GetValue(RangeMaxProperty); set => SetValue(RangeMaxProperty, value); }

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    protected override Size MeasureOverride(Size availableSize) => new(80, Math.Min(330, double.IsInfinity(availableSize.Height) ? 330 : availableSize.Height));

    protected override void OnRender(DrawingContext dc)
    {
        var r = new Rect(6, 8, 16, Math.Max(40, ActualHeight - 16));
        dc.DrawRectangle(Backdrop, null, new Rect(0, 0, ActualWidth, ActualHeight));
        int bands = ContourPalette.Bands;
        double bandH = r.Height / bands;
        for (int i = 0; i < bands; i++)
        {
            var brush = new SolidColorBrush(ContourPalette.ColorOf(i));
            brush.Freeze();
            dc.DrawRectangle(brush, null, new Rect(r.Left, r.Bottom - (i + 1) * bandH, r.Width, bandH + 0.5));
        }

        dc.DrawRectangle(null, Outline, r);
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (int i = 0; i <= bands; i += 2)
        {
            double v = RangeMin + (RangeMax - RangeMin) * i / bands;
            double y = r.Bottom - i * bandH;
            dc.DrawLine(Outline, new Point(r.Right, y), new Point(r.Right + 4, y));
            var ft = new FormattedText(ContourPalette.Format(v), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, 10.5, Ink, pixelsPerDip);
            dc.DrawText(ft, new Point(r.Right + 6, y - 7));
        }
    }
}
