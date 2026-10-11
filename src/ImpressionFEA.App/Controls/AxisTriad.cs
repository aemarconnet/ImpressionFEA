using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace ImpressionFEA.App.Controls;

/// <summary>
/// Orientation triad for 3D views: model X (red), Y (green) and Z (blue, the press axis) as labelled arrows, drawn
/// in 2D from the camera's right / up / look directions so it always matches the view. Back-facing axes are drawn first
/// and lighter.
/// </summary>
public sealed class AxisTriad : FrameworkElement
{
    private static readonly (string Name, Vector3D Dir, Color Color)[] Axes =
    [
        // Model axes expressed in view space (view X = model x, view Y = model z, view Z = −model y).
        ("X", new Vector3D(1, 0, 0), Color.FromRgb(0xD6, 0x27, 0x28)),
        ("Y", new Vector3D(0, 0, -1), Color.FromRgb(0x2C, 0xA0, 0x2C)),
        ("Z", new Vector3D(0, 1, 0), Color.FromRgb(0x1F, 0x5F, 0xD0)),
    ];

    private static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    private Vector3D _right = new(1, 0, 0);
    private Vector3D _up = new(0, 1, 0);
    private Vector3D _look = new(0, 0, -1);

    public AxisTriad()
    {
        Width = 84;
        Height = 84;
        IsHitTestVisible = false;
    }

    /// <summary>Updates the triad from the camera basis (view space).</summary>
    public void SetOrientation(Vector3D look, Vector3D up)
    {
        look.Normalize();
        var right = Vector3D.CrossProduct(look, up);
        right.Normalize();
        var u = Vector3D.CrossProduct(right, look);
        _look = look;
        _up = u;
        _right = right;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        double len = 0.36 * Math.Min(ActualWidth, ActualHeight);
        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        // Far axes first so the near ones are drawn on top.
        foreach (var (name, dir, color) in Axes.OrderByDescending(a => Vector3D.DotProduct(a.Dir, _look)))
        {
            double sx = Vector3D.DotProduct(dir, _right), sy = Vector3D.DotProduct(dir, _up);
            bool away = Vector3D.DotProduct(dir, _look) > 0.2;
            var col = away ? Color.FromArgb(0x90, color.R, color.G, color.B) : color;
            var brush = new SolidColorBrush(col);
            brush.Freeze();
            var pen = new Pen(brush, 2.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();
            var tip = new Point(c.X + len * sx, c.Y - len * sy);
            var d = tip - c;
            if (d.Length > 2)
            {
                d.Normalize();
                var n = new Vector(-d.Y, d.X);
                dc.DrawLine(pen, c, tip - 5 * d);
                var head = new StreamGeometry();
                using (var g = head.Open())
                {
                    g.BeginFigure(tip, true, true);
                    g.LineTo(tip - 8 * d + 4 * n, true, false);
                    g.LineTo(tip - 8 * d - 4 * n, true, false);
                }

                head.Freeze();
                dc.DrawGeometry(brush, null, head);
            }
            else
            {
                dc.DrawEllipse(brush, null, c, 3, 3); // pointing straight at / away from the viewer
            }

            var label = new FormattedText(name, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 12, brush, ppd);
            var lp = new Point(c.X + (len + 9) * sx - label.Width / 2, c.Y - (len + 9) * sy - label.Height / 2);
            dc.DrawText(label, lp);
        }

        dc.DrawEllipse(Brushes.DimGray, null, c, 2, 2);
    }
}
