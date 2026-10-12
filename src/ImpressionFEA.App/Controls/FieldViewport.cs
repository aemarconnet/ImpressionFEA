using System.Globalization;
using System.Windows.Input;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImpressionFEA.Core.Geometry;
using ImpressionFEA.Core.Simulation;

namespace ImpressionFEA.App.Controls;

/// <summary>
/// Native results viewport. Element fields are averaged to nodes and rasterised per triangle (each quad split in
/// two) into a WriteableBitmap with banded contour colours; dies, mesh edges, contact nodes, the symmetry axis,
/// a colour bar and a scale bar are drawn with WPF vector primitives on top.
/// </summary>
public sealed class FieldViewport : FrameworkElement
{
    public static readonly DependencyProperty SnapshotProperty = Reg(nameof(Snapshot), typeof(ForgingSnapshot), null);
    public static readonly DependencyProperty DieProperty = Reg(nameof(Die), typeof(DieSet), null);
    public static readonly DependencyProperty FieldProperty = Reg(nameof(Field), typeof(ResultField), ResultField.VonMisesStress);
    public static readonly DependencyProperty RangeMinProperty = Reg(nameof(RangeMin), typeof(double), 0.0);
    public static readonly DependencyProperty RangeMaxProperty = Reg(nameof(RangeMax), typeof(double), 1.0);
    public static readonly DependencyProperty FieldTitleProperty = Reg(nameof(FieldTitle), typeof(string), "");
    public static readonly DependencyProperty ShowMeshProperty = Reg(nameof(ShowMesh), typeof(bool), true);
    public static readonly DependencyProperty MirrorProperty = Reg(nameof(Mirror), typeof(bool), true);
    public static readonly DependencyProperty ShowContactProperty = Reg(nameof(ShowContact), typeof(bool), false);
    public static readonly DependencyProperty ShowFieldProperty = Reg(nameof(ShowField), typeof(bool), true);
    public static readonly DependencyProperty ShowVelocityProperty = Reg(nameof(ShowVelocity), typeof(bool), false);
    public static readonly DependencyProperty ArrowSpacingProperty = Reg(nameof(ArrowSpacing), typeof(double), 1.6);
    public static readonly DependencyProperty FlowLinesProperty = Reg(nameof(FlowLines), typeof(IReadOnlyList<Vec2[]>), null);

    /// <summary>Grain flow lines of the shown increment (right half, mm); null = none.</summary>
    public IReadOnlyList<Vec2[]>? FlowLines { get => (IReadOnlyList<Vec2[]>?)GetValue(FlowLinesProperty); set => SetValue(FlowLinesProperty, value); }
    public static readonly DependencyProperty ClipLowProperty = DependencyProperty.Register(nameof(ClipLow), typeof(double), typeof(FieldViewport),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty ClipHighProperty = DependencyProperty.Register(nameof(ClipHigh), typeof(double), typeof(FieldViewport),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    /// <summary>Iso-clip: lower end of the visible range (fraction of the colour range).</summary>
    public double ClipLow { get => (double)GetValue(ClipLowProperty); set => SetValue(ClipLowProperty, value); }

    /// <summary>Iso-clip: upper end of the visible range (fraction of the colour range).</summary>
    public double ClipHigh { get => (double)GetValue(ClipHighProperty); set => SetValue(ClipHighProperty, value); }

    private Rect _legend = Rect.Empty;
    private int _clipDrag;

    /// <summary>Element shown under the iso-clip range (always true without clipping or without a field).</summary>
    private Func<int, bool> ClipFilter(StepSnapshot s)
    {
        double lo = ClipLow, hi = ClipHigh;
        if (!ShowField || (lo <= 0 && hi >= 1)) return _ => true;
        var v = s.Field(Field);
        double a = RangeMin, span = RangeMax - RangeMin;
        if (!(span > 0)) return _ => true;
        return e => (v[e] - a) / span >= lo - 1e-9 && (v[e] - a) / span <= hi + 1e-9;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var pt = e.GetPosition(this);
        if (_legend.IsEmpty || !new Rect(_legend.X - 10, _legend.Y - 8, _legend.Width + 20, _legend.Height + 16).Contains(pt)) return;
        e.Handled = true;
        if (e.ClickCount == 2)
        {
            ClipLow = 0;
            ClipHigh = 1;
            return;
        }

        var (h, f) = ColorBar.Hit(_legend, pt.Y, ClipLow, ClipHigh);
        _clipDrag = h;
        ApplyClip(f);
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var pt = e.GetPosition(this);
        Cursor = !_legend.IsEmpty && new Rect(_legend.X - 10, _legend.Y - 8, _legend.Width + 20, _legend.Height + 16).Contains(pt) ? Cursors.SizeNS : null;
        if (_clipDrag == 0) return;
        ApplyClip(Math.Clamp((_legend.Bottom - pt.Y) / _legend.Height, 0, 1));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _clipDrag = 0;
        ReleaseMouseCapture();
    }

    private void ApplyClip(double f) => (ClipLow, ClipHigh) = ColorBar.Drag(_clipDrag, f, ClipLow, ClipHigh, ColorBar.SnapToBands);
    public static readonly DependencyProperty ArrowScaleProperty = Reg(nameof(ArrowScale), typeof(double), 1.0);

    /// <summary>Arrow spacing (1.6 = default density; smaller = denser).</summary>
    public double ArrowSpacing { get => (double)GetValue(ArrowSpacingProperty); set => SetValue(ArrowSpacingProperty, value); }

    /// <summary>Arrow length multiplier.</summary>
    public double ArrowScale { get => (double)GetValue(ArrowScaleProperty); set => SetValue(ArrowScaleProperty, value); }
    public static readonly DependencyProperty UnitsProperty = Reg(nameof(Units), typeof(ImpressionFEA.App.Infrastructure.UnitSystem), ImpressionFEA.App.Infrastructure.UnitSystem.US);

    /// <summary>Display unit system (labels and scale bar); changing it redraws.</summary>
    public ImpressionFEA.App.Infrastructure.UnitSystem Units { get => (ImpressionFEA.App.Infrastructure.UnitSystem)GetValue(UnitsProperty); set => SetValue(UnitsProperty, value); }
    public static readonly DependencyProperty ViewBoundsProperty = Reg(nameof(ViewBounds), typeof(Rect), new Rect(0, -10, 50, 60));

    private const int Bands = ContourPalette.Bands;
    private const double ColorBarWidth = 84;
    private static readonly Typeface MonoFace = new(new FontFamily("Cascadia Mono, Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface UiFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Brush Background = Frozen(new SolidColorBrush(Color.FromRgb(0xF7, 0xF8, 0xFA)));
    private static readonly Brush DieFill = Frozen(new SolidColorBrush(Color.FromRgb(0x8A, 0x93, 0x9E)));
    private static readonly Pen DiePen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x3A, 0x41, 0x4A))), 1.2));
    private static readonly Pen MeshPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x70, 0x10, 0x18, 0x20))), 0.5));
    private static readonly Pen OutlinePen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x10, 0x14, 0x18))), 1.1));
    private static readonly Pen AxisPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x56, 0x60, 0x6C))), 1) { DashStyle = new DashStyle([8, 3, 2, 3], 0) });
    private static readonly Brush BottomContact = Frozen(new SolidColorBrush(Color.FromRgb(0xE6, 0x9F, 0x00)));
    private static readonly Brush TopContact = Frozen(new SolidColorBrush(Color.FromRgb(0xCC, 0x3D, 0x9B)));
    private static readonly Brush Ink = Frozen(new SolidColorBrush(Color.FromRgb(0x10, 0x14, 0x18)));
    private static readonly Brush BilletFlat = Frozen(new SolidColorBrush(Color.FromRgb(0xD6, 0xDE, 0xE8)));

    private WriteableBitmap? _bitmap;
    private int[] _pixels = [];
    private readonly int[] _palette = ContourPalette.Colors;

    private static DependencyProperty Reg(string name, Type type, object? def) =>
        DependencyProperty.Register(name, type, typeof(FieldViewport), new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsRender));

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    public ForgingSnapshot? Snapshot { get => (ForgingSnapshot?)GetValue(SnapshotProperty); set => SetValue(SnapshotProperty, value); }

    public DieSet? Die { get => (DieSet?)GetValue(DieProperty); set => SetValue(DieProperty, value); }

    public ResultField Field { get => (ResultField)GetValue(FieldProperty); set => SetValue(FieldProperty, value); }

    public double RangeMin { get => (double)GetValue(RangeMinProperty); set => SetValue(RangeMinProperty, value); }

    public double RangeMax { get => (double)GetValue(RangeMaxProperty); set => SetValue(RangeMaxProperty, value); }

    public string FieldTitle { get => (string)GetValue(FieldTitleProperty); set => SetValue(FieldTitleProperty, value); }

    public bool ShowMesh { get => (bool)GetValue(ShowMeshProperty); set => SetValue(ShowMeshProperty, value); }

    public bool Mirror { get => (bool)GetValue(MirrorProperty); set => SetValue(MirrorProperty, value); }

    public bool ShowContact { get => (bool)GetValue(ShowContactProperty); set => SetValue(ShowContactProperty, value); }

    /// <summary>False for the pre-run preview: the billet is drawn flat, without a contour legend.</summary>
    public bool ShowField { get => (bool)GetValue(ShowFieldProperty); set => SetValue(ShowFieldProperty, value); }

    /// <summary>Overlay arrows of the nodal material velocity (in-plane components).</summary>
    public bool ShowVelocity { get => (bool)GetValue(ShowVelocityProperty); set => SetValue(ShowVelocityProperty, value); }

    private static readonly Brush ArrowFill = Frozen(new SolidColorBrush(Color.FromRgb(0x10, 0x14, 0x18)));
    private static readonly Pen ArrowPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x10, 0x14, 0x18))), 1.3));
    private static readonly Pen ArrowHalo = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF))), 3.2));

    /// <summary>
    /// Velocity arrows: one node per ~24 px screen cell (so arrows never crowd), length ∝ speed (longest = 22 px), with a
    /// light halo so they read on any contour colour. Returns the largest speed, mm/s.
    /// </summary>
    private double DrawVelocity(DrawingContext dc, StepSnapshot s, Func<double, double, Point> P)
    {
        if (!s.HasVelocity) return 0;
        double vmax = 0;
        for (int n = 0; n < s.NodeCount; n++) vmax = Math.Max(vmax, Math.Sqrt(s.VelocityX[n] * s.VelocityX[n] + s.VelocityY[n] * s.VelocityY[n]));
        if (vmax <= 0) return 0;
        double cell = 15 * Math.Clamp(ArrowSpacing, 0.5, 6), maxLen = 22 * Math.Clamp(ArrowScale, 0.1, 5);
        var used = new HashSet<(int, int)>();
        var shafts = new StreamGeometry();
        var heads = new StreamGeometry();
        using (var gs = shafts.Open())
        using (var gh = heads.Open())
        {
            foreach (double mirror in Mirror ? new[] { 1.0, -1.0 } : [1.0])
            {
                for (int n = 0; n < s.NodeCount; n++)
                {
                    var a = P(mirror * s.X[n], s.Y[n]);
                    if (!used.Add(((int)Math.Floor(a.X / cell), (int)Math.Floor(a.Y / cell)))) continue;
                    double vx = mirror * s.VelocityX[n], vy = s.VelocityY[n];
                    double sp = Math.Sqrt(vx * vx + vy * vy);
                    if (sp < 1e-3 * vmax) continue;
                    double len = Math.Max(3, maxLen * sp / vmax);
                    var d = new Vector(vx / sp, -vy / sp); // screen y points down
                    var tip = a + len * d;
                    var nrm = new Vector(-d.Y, d.X);
                    double hl = Math.Min(6 * Math.Sqrt(Math.Clamp(ArrowScale, 0.1, 5)), 0.45 * len), hw = 0.5 * hl;
                    gs.BeginFigure(a, false, false);
                    gs.LineTo(tip - hl * 0.6 * d, true, false);
                    gh.BeginFigure(tip, true, true);
                    gh.LineTo(tip - hl * d + hw * nrm, true, false);
                    gh.LineTo(tip - hl * d - hw * nrm, true, false);
                }
            }
        }

        shafts.Freeze();
        heads.Freeze();
        dc.DrawGeometry(null, ArrowHalo, shafts);
        dc.DrawGeometry(ArrowFill, ArrowHalo, heads);
        dc.DrawGeometry(null, ArrowPen, shafts);
        dc.DrawGeometry(ArrowFill, null, heads);
        return vmax;
    }

    /// <summary>World window (mm) of the right half: X = 0 … extent, Y range.</summary>
    public Rect ViewBounds { get => (Rect)GetValue(ViewBoundsProperty); set => SetValue(ViewBoundsProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Background, null, new Rect(0, 0, w, h));
        if (w < 80 || h < 60) return;

        bool legend = ShowField && Snapshot is StepSnapshot;
        var plot = new Rect(12, 28, Math.Max(10, w - 24 - (legend ? ColorBarWidth : 0)), Math.Max(10, h - 56));
        var vb = ViewBounds;
        double x0 = Mirror ? -vb.Right : vb.Left;
        double worldW = vb.Right - x0, worldH = vb.Height;
        double scale = Math.Min(plot.Width / worldW, plot.Height / worldH);
        double ox = plot.Left + (plot.Width - worldW * scale) / 2 - x0 * scale;
        double oy = plot.Top + (plot.Height - worldH * scale) / 2 + vb.Bottom * scale;
        Point P(double x, double y) => new(ox + x * scale, oy - y * scale);

        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h)));
        var snap = Snapshot as StepSnapshot;
        if (snap is not null) DrawBillet(dc, snap, P, w, h);
        double vmax = snap is not null && ShowVelocity ? DrawVelocity(dc, snap, P) : 0;
        if (Die is { } die) DrawDies(dc, die, snap?.DieGap ?? 0, P, x0, vb);
        dc.DrawLine(AxisPen, P(0, vb.Top - 5), P(0, vb.Bottom + 5));
        dc.Pop();

        Text(dc, FieldTitle, new Point(12, 7), 12, UiFace, Ink);
        if (vmax > 0) Text(dc, "Flow arrows: material velocity, longest = " + ImpressionFEA.App.Infrastructure.Units.Current.Speedf(vmax), new Point(12, 24), 11, UiFace, Ink);
        DrawScaleBar(dc, scale, new Point(14, h - 18));
        _legend = legend ? new Rect(w - ColorBarWidth + 6, 34, 16, Math.Min(320, h - 70)) : Rect.Empty;
        if (legend) DrawColorBar(dc, _legend);
    }

    private void DrawBillet(DrawingContext dc, StepSnapshot s, Func<double, double, Point> P, double w, double h)
    {
        var mesh = s.Mesh;
        var conn = mesh.Connectivity;
        if (ShowField)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            int pw = Math.Max(1, (int)(w * dpi.DpiScaleX)), ph = Math.Max(1, (int)(h * dpi.DpiScaleY));
            if (_bitmap is null || _bitmap.PixelWidth != pw || _bitmap.PixelHeight != ph)
            {
                _bitmap = new WriteableBitmap(pw, ph, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32, null);
                _pixels = new int[pw * ph];
            }

            Array.Clear(_pixels);
            var nodal = ContourPalette.NodalField(s, Field);
            double lo = RangeMin, hi = RangeMax;
            double inv = hi > lo ? 1.0 / (hi - lo) : 0;
            double sx = dpi.DpiScaleX, sy = dpi.DpiScaleY;
            var visible = ClipFilter(s);
            foreach (double mirror in Mirror ? new[] { 1.0, -1.0 } : [1.0])
            {
                for (int e = 0; e < mesh.ElementCount; e++)
                {
                    if (!visible(e)) continue;
                    int a = conn[4 * e], b = conn[4 * e + 1], c = conn[4 * e + 2], d = conn[4 * e + 3];
                    var pa = P(mirror * s.X[a], s.Y[a]);
                    var pb = P(mirror * s.X[b], s.Y[b]);
                    var pc = P(mirror * s.X[c], s.Y[c]);
                    var pd = P(mirror * s.X[d], s.Y[d]);
                    Raster(pw, ph, pa.X * sx, pa.Y * sy, (nodal[a] - lo) * inv, pb.X * sx, pb.Y * sy, (nodal[b] - lo) * inv, pc.X * sx, pc.Y * sy, (nodal[c] - lo) * inv);
                    Raster(pw, ph, pa.X * sx, pa.Y * sy, (nodal[a] - lo) * inv, pc.X * sx, pc.Y * sy, (nodal[c] - lo) * inv, pd.X * sx, pd.Y * sy, (nodal[d] - lo) * inv);
                }
            }

            _bitmap.WritePixels(new Int32Rect(0, 0, pw, ph), _pixels, pw * 4, 0);
            dc.DrawImage(_bitmap, new Rect(0, 0, w, h));
        }

        var outline = new StreamGeometry();
        using (var g = outline.Open())
        {
            foreach (double mirror in Mirror ? new[] { 1.0, -1.0 } : [1.0])
            {
                var loop = mesh.BoundaryLoop;
                g.BeginFigure(P(mirror * s.X[loop[0]], s.Y[loop[0]]), !ShowField, true);
                for (int k = 1; k < loop.Length; k++) g.LineTo(P(mirror * s.X[loop[k]], s.Y[loop[k]]), true, false);
            }
        }

        outline.Freeze();
        if (!ShowField) dc.DrawGeometry(BilletFlat, null, outline);

        if (ShowMesh)
        {
            var geo = new StreamGeometry();
            var meshVisible = ClipFilter(s);
            using (var g = geo.Open())
            {
                foreach (double mirror in Mirror ? new[] { 1.0, -1.0 } : [1.0])
                {
                    for (int e = 0; e < mesh.ElementCount; e++)
                    {
                        if (!meshVisible(e)) continue;
                        g.BeginFigure(P(mirror * s.X[conn[4 * e]], s.Y[conn[4 * e]]), false, true);
                        for (int a = 1; a < 4; a++) g.LineTo(P(mirror * s.X[conn[4 * e + a]], s.Y[conn[4 * e + a]]), true, false);
                    }
                }
            }

            geo.Freeze();
            dc.DrawGeometry(null, MeshPen, geo);
        }

        if (FlowLines is { Count: > 0 } flow) DrawFlowLines(dc, flow, P);

        dc.DrawGeometry(null, OutlinePen, outline);

        if (ShowContact)
        {
            // Boundary segments whose two nodes touch the same die, drawn thick in that die's colour.
            var loop = mesh.BoundaryLoop;
            foreach (double mirror in Mirror ? new[] { 1.0, -1.0 } : [1.0])
            {
                for (int k = 0; k < loop.Length; k++)
                {
                    int a = loop[k], b = loop[(k + 1) % loop.Length];
                    var c = s.Contact[a];
                    if (c == NodeContact.Free || s.Contact[b] != c) continue;
                    dc.DrawLine(c == NodeContact.BottomDie ? BottomContactPen : TopContactPen, P(mirror * s.X[a], s.Y[a]), P(mirror * s.X[b], s.Y[b]));
                }
            }
        }
    }

    private static readonly Pen FlowPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x14, 0x18, 0x1E))), 1.0) { LineJoin = PenLineJoin.Round });
    private static readonly Pen FlowHalo = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF))), 2.6) { LineJoin = PenLineJoin.Round });

    /// <summary>Grain flow lines as thin dark polylines with a light halo (readable on any contour colour), mirrored with the part.</summary>
    private void DrawFlowLines(DrawingContext dc, IReadOnlyList<Vec2[]> lines, Func<double, double, Point> P)
    {
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            foreach (double mirror in Mirror ? new[] { 1.0, -1.0 } : [1.0])
            {
                foreach (var line in lines)
                {
                    if (line.Length < 2) continue;
                    g.BeginFigure(P(mirror * line[0].X, line[0].Y), false, false);
                    var pts = new Point[line.Length - 1];
                    for (int i = 1; i < line.Length; i++) pts[i - 1] = P(mirror * line[i].X, line[i].Y);
                    g.PolyLineTo(pts, true, false);
                }
            }
        }

        geo.Freeze();
        dc.DrawGeometry(null, FlowHalo, geo);
        dc.DrawGeometry(null, FlowPen, geo);
    }

    private static readonly Pen BottomContactPen = Frozen(new Pen(BottomContact, 4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    private static readonly Pen TopContactPen = Frozen(new Pen(TopContact, 4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });

    private static void DrawDies(DrawingContext dc, DieSet die, double gap, Func<double, double, Point> P, double x0, Rect vb)
    {
        double far = (vb.Height + vb.Width) * 4;
        foreach (var (surf, off, below) in new[] { (die.Bottom, 0.0, true), (die.TopClosed, gap, false) })
        {
            var pts = new List<Point>();
            var src = surf.Points.Where(p => p.X <= vb.Right + 2).ToList();
            if (src[^1].X < vb.Right + 2) src.Add(new Vec2(vb.Right + 2, surf.HeightAt(vb.Right + 2)));
            if (x0 < 0)
            {
                for (int i = src.Count - 1; i >= 1; i--) pts.Add(P(-src[i].X, src[i].Y + off));
            }

            pts.AddRange(src.Select(p => P(p.X, p.Y + off)));
            double yEdge = below ? vb.Top - far / 4 : vb.Bottom + far / 4;
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(new Point(pts[0].X, P(0, yEdge).Y), true, true);
                foreach (var p in pts) g.LineTo(p, true, false);
                g.LineTo(new Point(pts[^1].X, P(0, yEdge).Y), true, false);
            }

            geo.Freeze();
            dc.DrawGeometry(DieFill, DiePen, geo);
        }
    }

    private void DrawColorBar(DrawingContext dc, Rect r)
    {
        double bandH = r.Height / Bands;
        for (int i = 0; i < Bands; i++)
        {
            int c = _palette[i];
            var brush = new SolidColorBrush(Color.FromRgb((byte)(c >> 16), (byte)(c >> 8), (byte)c));
            brush.Freeze();
            dc.DrawRectangle(brush, null, new Rect(r.Left, r.Bottom - (i + 1) * bandH, r.Width, bandH + 0.5));
        }

        double lo = RangeMin, span = RangeMax - RangeMin;
        var field = Field;
        ColorBar.DrawClip(dc, r, ClipLow, ClipHigh, f => ContourPalette.Label(lo + f * span, field), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawRectangle(null, OutlinePen, r);
        for (int i = 0; i <= Bands; i += 2)
        {
            double v = RangeMin + (RangeMax - RangeMin) * i / Bands;
            double y = r.Bottom - i * bandH;
            dc.DrawLine(OutlinePen, new Point(r.Right, y), new Point(r.Right + 4, y));
            Text(dc, ContourPalette.Label(v, Field), new Point(r.Right + 6, y - 7), 10.5, MonoFace, Ink);
        }
    }

    private void DrawScaleBar(DrawingContext dc, double scale, Point at)
    {
        var u = ImpressionFEA.App.Infrastructure.Units.Current;
        double[] options = u.System == ImpressionFEA.App.Infrastructure.UnitSystem.US ? [0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2, 4] : [0.5, 1, 2, 5, 10, 20, 50, 100];
        double nice = options.LastOrDefault(o => o * u.MmPerLength * scale <= 120, options[0]);
        double len = nice * u.MmPerLength * scale;
        dc.DrawLine(OutlinePen, at, new Point(at.X + len, at.Y));
        dc.DrawLine(OutlinePen, new Point(at.X, at.Y - 4), new Point(at.X, at.Y + 4));
        dc.DrawLine(OutlinePen, new Point(at.X + len, at.Y - 4), new Point(at.X + len, at.Y + 4));
        Text(dc, nice.ToString(CultureInfo.InvariantCulture) + " " + u.Length, new Point(at.X + len + 6, at.Y - 7), 10.5, MonoFace, Ink);
    }

    private void Text(DrawingContext dc, string text, Point at, double size, Typeface face, Brush brush)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(ft, at);
    }

    private void Raster(int pw, int ph, double ax, double ay, double ta, double bx, double by, double tb, double cx, double cy, double tc)
    {
        double area = (bx - ax) * (cy - ay) - (cx - ax) * (by - ay);
        if (Math.Abs(area) < 1e-9) return;
        int minX = Math.Max(0, (int)Math.Floor(Math.Min(ax, Math.Min(bx, cx))));
        int maxX = Math.Min(pw - 1, (int)Math.Ceiling(Math.Max(ax, Math.Max(bx, cx))));
        int minY = Math.Max(0, (int)Math.Floor(Math.Min(ay, Math.Min(by, cy))));
        int maxY = Math.Min(ph - 1, (int)Math.Ceiling(Math.Max(ay, Math.Max(by, cy))));
        double invA = 1.0 / area;
        const double eps = -1e-6;
        for (int y = minY; y <= maxY; y++)
        {
            double py = y + 0.5;
            int row = y * pw;
            for (int x = minX; x <= maxX; x++)
            {
                double px = x + 0.5;
                double w0 = ((bx - px) * (cy - py) - (cx - px) * (by - py)) * invA;
                double w1 = ((cx - px) * (ay - py) - (ax - px) * (cy - py)) * invA;
                double w2 = 1 - w0 - w1;
                if (w0 < eps || w1 < eps || w2 < eps) continue;
                double t = w0 * ta + w1 * tb + w2 * tc;
                int band = (int)(t * Bands);
                _pixels[row + x] = _palette[Math.Clamp(band, 0, Bands - 1)];
            }
        }
    }
}
