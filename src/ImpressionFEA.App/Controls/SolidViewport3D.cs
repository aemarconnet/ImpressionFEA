using System.Windows;
using System.Runtime.CompilerServices;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using ImpressionFEA.Core.Geometry;
using ImpressionFEA.Core.Simulation;
using ImpressionFEA.Core.ThreeD;

namespace ImpressionFEA.App.Controls;

/// <summary>
/// 3D solid view: 2D results revolved (axisymmetric) or mirrored and extruded (plane strain), and true 3D tetrahedral
/// results (mirrored symmetric models or full imported models). Contours are mapped through a banded palette texture so
/// bands stay crisp; an optional cut-away exposes the section. Dies are translucent solids. SolidWorks-style controls:
/// middle-drag rotate, Ctrl+middle pan, Shift+middle zoom, Alt+middle roll, wheel zooms about the cursor, F fits,
/// Ctrl+1…7 standard views, Space opens the view menu, arrows rotate 15° (Shift 90°, Ctrl pans, Alt rolls).
/// </summary>
public sealed partial class SolidViewport3D : Grid
{
    public static readonly DependencyProperty SnapshotProperty = Reg(nameof(Snapshot), typeof(ForgingSnapshot), null, false);
    public static readonly DependencyProperty Die3DProperty = Reg(nameof(Die3D), typeof(DieSet3D), null, true);
    public static readonly DependencyProperty Bounds3DProperty = Reg(nameof(Bounds3D), typeof(Rect3D), new Rect3D(-50, -50, -10, 100, 100, 60), true);
    public static readonly DependencyProperty DieProperty = Reg(nameof(Die), typeof(DieSet), null, true);
    public static readonly DependencyProperty FieldProperty = Reg(nameof(Field), typeof(ResultField), ResultField.VonMisesStress, false);
    public static readonly DependencyProperty RangeMinProperty = Reg(nameof(RangeMin), typeof(double), 0.0, false);
    public static readonly DependencyProperty RangeMaxProperty = Reg(nameof(RangeMax), typeof(double), 1.0, false);
    public static readonly DependencyProperty ShowFieldProperty = Reg(nameof(ShowField), typeof(bool), true, false);
    public static readonly DependencyProperty CutAwayProperty = Reg(nameof(CutAway), typeof(bool), false, false);
    public static readonly DependencyProperty ShowDiesProperty = Reg(nameof(ShowDies), typeof(bool), true, false);
    public static readonly DependencyProperty ShowEdgesProperty = Reg(nameof(ShowEdges), typeof(bool), false, false);
    public static readonly DependencyProperty ShowDieEdgesProperty = Reg(nameof(ShowDieEdges), typeof(bool), false, false);
    public static readonly DependencyProperty GrainFlowProperty = Reg(nameof(GrainFlow), typeof(bool), false, false);
    public static readonly DependencyProperty AveragingProperty = Reg(nameof(Averaging), typeof(FieldAveraging), FieldAveraging.Nodal, false);

    /// <summary>Contour colours from nodal averages (smooth) or each element's own value (flat per element).</summary>
    public FieldAveraging Averaging { get => (FieldAveraging)GetValue(AveragingProperty); set => SetValue(AveragingProperty, value); }
    public static readonly DependencyProperty FlowLinesProperty = Reg(nameof(FlowLines), typeof(IReadOnlyList<Vec2[]>), null, false);
    public static readonly DependencyProperty FlowFieldProperty = Reg(nameof(FlowField), typeof(GrainReference3D), null, false);
    public static readonly DependencyProperty FlowFiberProperty = Reg(nameof(FlowFiber), typeof(FiberDirection), FiberDirection.Vertical, false);
    public static readonly DependencyProperty FlowLineCountProperty = Reg(nameof(FlowLineCount), typeof(int), 16, false);

    /// <summary>Grain flow is shown: plain part, a flat-cut section (3D results) and fibre lines on the cut faces.</summary>
    public bool GrainFlow { get => (bool)GetValue(GrainFlowProperty); set => SetValue(GrainFlowProperty, value); }

    /// <summary>2D results: flow lines of the shown increment (r / x, z), drawn on the cut faces of the revolved or extruded part.</summary>
    public IReadOnlyList<Vec2[]>? FlowLines { get => (IReadOnlyList<Vec2[]>?)GetValue(FlowLinesProperty); set => SetValue(FlowLinesProperty, value); }

    /// <summary>3D results: reference (billet) positions of the shown increment; fibre lines are its iso-lines on the section.</summary>
    public GrainReference3D? FlowField { get => (GrainReference3D?)GetValue(FlowFieldProperty); set => SetValue(FlowFieldProperty, value); }

    /// <summary>Fibre direction of the billet (resolved: Vertical = model z, Horizontal = model x).</summary>
    public FiberDirection FlowFiber { get => (FiberDirection)GetValue(FlowFiberProperty); set => SetValue(FlowFiberProperty, value); }

    /// <summary>Number of fibre lines across the billet.</summary>
    public int FlowLineCount { get => (int)GetValue(FlowLineCountProperty); set => SetValue(FlowLineCountProperty, value); }
    public static readonly DependencyProperty ShowVelocityProperty = Reg(nameof(ShowVelocity), typeof(bool), false, false);
    public static readonly DependencyProperty ShowContactProperty = Reg(nameof(ShowContact), typeof(bool), false, false);
    public static readonly DependencyProperty UnitsProperty = Reg(nameof(Units), typeof(ImpressionFEA.App.Infrastructure.UnitSystem), ImpressionFEA.App.Infrastructure.UnitSystem.US, false);

    /// <summary>Display unit system (colour-bar labels); changing it rebuilds.</summary>
    public ImpressionFEA.App.Infrastructure.UnitSystem Units { get => (ImpressionFEA.App.Infrastructure.UnitSystem)GetValue(UnitsProperty); set => SetValue(UnitsProperty, value); }
    public static readonly DependencyProperty LightingProperty = DependencyProperty.Register(nameof(Lighting), typeof(ViewLighting), typeof(SolidViewport3D),
        new PropertyMetadata(ViewLighting.Shaded, (d, _) => ((SolidViewport3D)d).ApplyLighting()));
    public static readonly DependencyProperty PartLengthProperty = Reg(nameof(PartLength), typeof(double), 100.0, true);
    public static readonly DependencyProperty ViewBoundsProperty = Reg(nameof(ViewBounds), typeof(Rect), new Rect(0, -10, 50, 60), true);
    public static readonly DependencyProperty FieldTitleProperty = DependencyProperty.Register(nameof(FieldTitle), typeof(string), typeof(SolidViewport3D),
        new PropertyMetadata("", (d, e) => ((SolidViewport3D)d)._title.Text = (string)e.NewValue));

    private const int FullSegments = 96;
    private static readonly Color DieColor = Color.FromArgb(0x55, 0x6E, 0x78, 0x84);
    private static readonly Color PlainColor = Color.FromRgb(0xC3, 0xCD, 0xD9);

    private readonly PerspectiveCamera _camera = new() { FieldOfView = 35, NearPlaneDistance = 1, FarPlaneDistance = 1e5 };
    private readonly DirectionalLight _headlight = new(Color.FromRgb(0xC8, 0xC8, 0xC8), new Vector3D(-1, -1, -1));
    private readonly AmbientLight _ambient = new(Colors.White);
    private readonly DirectionalLight _fill = new(Color.FromRgb(0x40, 0x40, 0x40), new Vector3D(0.3, -1, 0.2));
    private readonly Material _edgeMaterial = Frozen(new DiffuseMaterial(EdgeBrush()));
    private readonly Material _ghostMaterial = Frozen(new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(0x40, 0x8A, 0x96, 0xA4))));
    private static readonly Color BottomContactColor = Color.FromRgb(0xE6, 0x9F, 0x00);
    private static readonly Color TopContactColor = Color.FromRgb(0xCC, 0x3D, 0x9B);
    private readonly Material _bottomContactMaterial = Frozen(new DiffuseMaterial(new SolidColorBrush(BottomContactColor)));
    private readonly Material _topContactMaterial = Frozen(new DiffuseMaterial(new SolidColorBrush(TopContactColor)));
    private readonly ModelVisual3D _content = new();
    private readonly Grid _views = new();
    private readonly List<AuxView> _aux = [];
    private readonly Viewport3D _mainView;
    private readonly TextBlock _isoLabel;
    private readonly AxisTriad _triad = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(6, 0, 0, 26) };
    private readonly TextBlock _title = new() { Margin = new Thickness(12, 7, 0, 0), FontWeight = FontWeights.SemiBold, FontSize = 12 };
    private readonly ColorBar _bar = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 30, 12, 0), Width = 84 };
    private readonly Material _fieldMaterial;
    private readonly Material _plainMaterial;
    private readonly Material _dieEdgeMaterial;

    private Point3D _target;
    private double _distance = 200;
    private Vector3D _look = new(0, 0, -1); // unit, camera → target
    private Vector3D _up = new(0, 1, 0);    // unit, ⟂ _look
    private Point _lastMouse;
    private DragMode _drag;

    private enum DragMode
    {
        None,
        Rotate,
        Pan,
        Zoom,
        Roll,
    }

    /// <summary>Standard views (SolidWorks numbering: Ctrl+1 … Ctrl+7). Model z (press axis) is up.</summary>
    public enum StandardView
    {
        Front = 1,
        Back,
        Left,
        Right,
        Top,
        Bottom,
        Isometric,
    }

    public SolidViewport3D()
    {
        Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF8, 0xFA));
        ClipToBounds = true;
        Focusable = true;
        FocusVisualStyle = null;

        var strip = new ImageBrush(ContourPalette.Strip())
        {
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, 1, 1),
            TileMode = TileMode.None,
        };
        RenderOptions.SetBitmapScalingMode(strip, BitmapScalingMode.NearestNeighbor);
        strip.Freeze();
        _fieldMaterial = Frozen(new DiffuseMaterial(strip));
        _plainMaterial = Frozen(new MaterialGroup { Children = { new DiffuseMaterial(new SolidColorBrush(PlainColor)), new SpecularMaterial(new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50)), 30) } });
        _dieEdgeMaterial = Frozen(new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0x1E, 0x24, 0x2C))));

        var lights = new Model3DGroup();
        lights.Children.Add(_ambient);
        lights.Children.Add(_headlight);
        lights.Children.Add(_fill);
        ApplyLighting();
        var viewport = new Viewport3D { Camera = _camera, IsHitTestVisible = false };
        viewport.Children.Add(new ModelVisual3D { Content = lights });
        viewport.Children.Add(_content);
        RenderOptions.SetEdgeMode(viewport, EdgeMode.Unspecified);
        _mainView = viewport;

        _views.RowDefinitions.Add(new RowDefinition());
        _views.RowDefinitions.Add(new RowDefinition());
        _views.ColumnDefinitions.Add(new ColumnDefinition());
        _views.ColumnDefinitions.Add(new ColumnDefinition());
        _views.Children.Add(viewport);
        foreach (var (name, row, col) in new[] { ("TOP", 0, 0), ("FRONT", 1, 0), ("RIGHT", 1, 1) })
        {
            var aux = new AuxView(name);
            Grid.SetRow(aux.Host, row);
            Grid.SetColumn(aux.Host, col);
            _views.Children.Add(aux.Host);
            _aux.Add(aux);
        }

        _isoLabel = new TextBlock { Text = "ISOMETRIC", FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(0x56, 0x60, 0x6C)), Margin = new Thickness(8, 30, 0, 0), IsHitTestVisible = false };
        Grid.SetRow(_isoLabel, 0);
        Grid.SetColumn(_isoLabel, 1);
        _views.Children.Add(_isoLabel);
        _views.Children.Add(_triad);
        _views.SizeChanged += (_, _) => FitAux();
        LayoutViews();
        Children.Add(_views);
        Children.Add(_title);
        Children.Add(_bar);
        _bar.SetBinding(ColorBar.ClipLowProperty, new System.Windows.Data.Binding(nameof(ClipLow)) { Source = this, Mode = System.Windows.Data.BindingMode.TwoWay });
        _bar.SetBinding(ColorBar.ClipHighProperty, new System.Windows.Data.Binding(nameof(ClipHigh)) { Source = this, Mode = System.Windows.Data.BindingMode.TwoWay });
        Children.Add(new TextBlock
        {
            Text = "Middle-drag rotate · Ctrl+middle pan · Shift+middle / wheel zoom · Alt+middle roll · F fit · Ctrl+1–7 views · Space view menu · arrows rotate 15°",
            Foreground = new SolidColorBrush(Color.FromRgb(0x56, 0x60, 0x6C)),
            FontSize = 11,
            Margin = new Thickness(12, 0, 0, 8),
            VerticalAlignment = VerticalAlignment.Bottom,
            IsHitTestVisible = false,
        });

        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseUp += (_, _) => EndDrag();
        LostMouseCapture += (_, _) => _drag = DragMode.None;
        KeyDown += OnKeyDown;
        IsVisibleChanged += (_, _) => Rebuild();
        MouseWheel += (_, e) =>
        {
            Focus();
            ZoomAt(e.GetPosition(this), Math.Pow(0.88, e.Delta / 120.0));
        };
        SetView(StandardView.Isometric);
        Fit();
    }

    public ForgingSnapshot? Snapshot { get => (ForgingSnapshot?)GetValue(SnapshotProperty); set => SetValue(SnapshotProperty, value); }

    /// <summary>3D die set; when set, 3D (tetrahedral) results are rendered instead of revolved / extruded 2D sections.</summary>
    public DieSet3D? Die3D { get => (DieSet3D?)GetValue(Die3DProperty); set => SetValue(Die3DProperty, value); }

    /// <summary>World box (x, y, z in mm, z = press axis) used to frame the 3D analysis.</summary>
    public Rect3D Bounds3D { get => (Rect3D)GetValue(Bounds3DProperty); set => SetValue(Bounds3DProperty, value); }

    public DieSet? Die { get => (DieSet?)GetValue(DieProperty); set => SetValue(DieProperty, value); }

    public ResultField Field { get => (ResultField)GetValue(FieldProperty); set => SetValue(FieldProperty, value); }

    public double RangeMin { get => (double)GetValue(RangeMinProperty); set => SetValue(RangeMinProperty, value); }

    public double RangeMax { get => (double)GetValue(RangeMaxProperty); set => SetValue(RangeMaxProperty, value); }

    public bool ShowField { get => (bool)GetValue(ShowFieldProperty); set => SetValue(ShowFieldProperty, value); }

    public bool CutAway { get => (bool)GetValue(CutAwayProperty); set => SetValue(CutAwayProperty, value); }

    public bool ShowDies { get => (bool)GetValue(ShowDiesProperty); set => SetValue(ShowDiesProperty, value); }

    /// <summary>Out-of-plane length used to extrude plane-strain sections (mm).</summary>
    public double PartLength { get => (double)GetValue(PartLengthProperty); set => SetValue(PartLengthProperty, value); }

    public Rect ViewBounds { get => (Rect)GetValue(ViewBoundsProperty); set => SetValue(ViewBoundsProperty, value); }

    public string FieldTitle { get => (string)GetValue(FieldTitleProperty); set => SetValue(FieldTitleProperty, value); }

    private static DependencyProperty Reg(string name, Type type, object? def, bool refit) =>
        DependencyProperty.Register(name, type, typeof(SolidViewport3D), new PropertyMetadata(def, (d, _) =>
        {
            var v = (SolidViewport3D)d;
            if (refit) v.Fit();
            v.Rebuild();
        }));

    /// <summary>Uniform: ambient light only (no shading; contour colours exactly as on the colour bar). Soft / Shaded add a headlight.</summary>
    public ViewLighting Lighting { get => (ViewLighting)GetValue(LightingProperty); set => SetValue(LightingProperty, value); }

    /// <summary>3D arrows of the nodal material velocity through the volume (the part is drawn as a translucent ghost).</summary>
    public bool ShowVelocity { get => (bool)GetValue(ShowVelocityProperty); set => SetValue(ShowVelocityProperty, value); }

    /// <summary>Highlight surface faces in contact with the bottom die (amber) and the top die (magenta).</summary>
    public bool ShowContact { get => (bool)GetValue(ShowContactProperty); set => SetValue(ShowContactProperty, value); }

    /// <summary>Draw the element edges of the workpiece surface (and section).</summary>
    public bool ShowEdges { get => (bool)GetValue(ShowEdgesProperty); set => SetValue(ShowEdgesProperty, value); }

    /// <summary>Thin dark lines on the sharp edges of the die geometry, so dies read even when shading is flat.</summary>
    public bool ShowDieEdges { get => (bool)GetValue(ShowDieEdgesProperty); set => SetValue(ShowDieEdgesProperty, value); }

    private void ApplyLighting()
    {
        (byte amb, byte head, byte fill) = Lighting switch
        {
            ViewLighting.Shaded => ((byte)0x70, (byte)0xC8, (byte)0x40),
            ViewLighting.Soft => ((byte)0xB0, (byte)0x58, (byte)0x18),
            _ => ((byte)0xFF, (byte)0x00, (byte)0x00),
        };
        _ambient.Color = Color.FromRgb(amb, amb, amb);
        _headlight.Color = Color.FromRgb(head, head, head);
        _fill.Color = Color.FromRgb(fill, fill, fill);
        foreach (var a in _aux) a.SetLighting(amb, head);
    }

    /// <summary>
    /// Texture for the edge overlay: the outline of the right triangle (0,0)–(1,0)–(0,1) on a transparent background.
    /// Every surface triangle maps its corners to those UVs, so its three edges are drawn wherever it is.
    /// </summary>
    private static ImageBrush EdgeBrush()
    {
        const int n = 256;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(0xD0, 0x20, 0x26, 0x2E)), 14);
            pen.Freeze();
            dc.DrawLine(pen, new Point(0, 0), new Point(n, 0));
            dc.DrawLine(pen, new Point(0, 0), new Point(0, n));
            dc.DrawLine(pen, new Point(n, 0), new Point(0, n));
        }

        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(n, n, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        bmp.Freeze();
        var brush = new ImageBrush(bmp) { ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 1, 1), TileMode = TileMode.None };
        brush.Freeze();
        return brush;
    }

    /// <summary>Overlay copy of every triangle of <paramref name="g"/>, nudged outwards, textured with the triangle outline.</summary>
    private Model3D EdgeOverlay(MeshGeometry3D g)
    {
        var b = Bounds3D;
        double eps = 6e-4 * Math.Sqrt(b.SizeX * b.SizeX + b.SizeY * b.SizeY + b.SizeZ * b.SizeZ);
        var o = new MeshGeometry3D();
        var idx = g.TriangleIndices;
        for (int k = 0; k + 2 < idx.Count; k += 3)
        {
            for (int c = 0; c < 3; c++)
            {
                int i = idx[k + c];
                o.Positions.Add(g.Positions[i] + eps * g.Normals[i]);
                o.TextureCoordinates.Add(c == 0 ? new Point(0, 0) : c == 1 ? new Point(1, 0) : new Point(0, 1));
            }

            o.TriangleIndices.Add(k);
            o.TriangleIndices.Add(k + 1);
            o.TriangleIndices.Add(k + 2);
        }

        o.Freeze();
        var m = new GeometryModel3D(o, _edgeMaterial) { BackMaterial = _edgeMaterial };
        m.Freeze();
        return m;
    }

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    // ------------------------------------------------------------------ quad view

    private void SetContent(Model3DGroup group)
    {
        _content.Content = group;
        foreach (var a in _aux) a.Content.Content = group; // frozen, so shared by every view (framing stays fixed)
    }

    private void LayoutViews()
    {
        bool quad = QuadView;
        Grid.SetRow(_mainView, quad ? 0 : 0);
        Grid.SetColumn(_mainView, quad ? 1 : 0);
        Grid.SetRowSpan(_mainView, quad ? 1 : 2);
        Grid.SetColumnSpan(_mainView, quad ? 1 : 2);
        Grid.SetRow(_triad, quad ? 0 : 1);
        Grid.SetColumn(_triad, quad ? 1 : 0);
        _triad.Margin = new Thickness(6, 0, 0, quad ? 6 : 26);
        foreach (var a in _aux) a.Host.Visibility = quad ? Visibility.Visible : Visibility.Collapsed;
        _isoLabel.Visibility = quad ? Visibility.Visible : Visibility.Collapsed;
        if (quad)
        {
            foreach (var a in _aux) a.Content.Content = _content.Content;
            Dispatcher.BeginInvoke(FitAux, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    /// <summary>Frames the orthographic views on the visible bodies.</summary>
    /// <summary>
    /// Frames the orthographic views like an engineering drawing: one common scale and one common centre, so Top lines
    /// up with Front (same x), Front with Right (same heights), and features are the same size in every view. The frame
    /// is the whole job (both dies at their extreme positions), not the current content, so it never moves during the
    /// stroke: the bottom die stays put at the bottom of Front and Right while the top die and workpiece move.
    /// </summary>
    private void FitAux()
    {
        if (!QuadView || _aux.Count == 0) return;
        Rect3D v; // view space: X = model x, Y = model z (up), Z = -model y
        if (Die3D is not null)
        {
            var b = Bounds3D;
            v = new Rect3D(b.X, b.Z, -(b.Y + b.SizeY), b.SizeX, b.SizeZ, b.SizeY);
        }
        else
        {
            // 2D results shown as solids: revolved (radius R) or extruded (part length) sections.
            var vb = ViewBounds;
            double r = vb.Right, depth = Die?.IsPlaneStrain == true ? PartLength : 2 * r;
            v = new Rect3D(-r, vb.Top, -depth / 2, 2 * r, vb.Height, depth);
        }

        if (v.IsEmpty || v.SizeX <= 0) return;
        var cell = _aux[0].Size;
        double aspect = cell.Width > 1 && cell.Height > 1 ? cell.Width / cell.Height : 1;
        // Top: x across, y up · Front: x across, z up · Right: y across, z up.
        double width = 1.12 * Math.Max(Math.Max(v.SizeX, v.SizeZ * aspect), Math.Max(Math.Max(v.SizeX, v.SizeY * aspect), Math.Max(v.SizeZ, v.SizeY * aspect)));
        var centre = new Point3D(v.X + v.SizeX / 2, v.Y + v.SizeY / 2, v.Z + v.SizeZ / 2);
        double diag = Math.Sqrt(v.SizeX * v.SizeX + v.SizeY * v.SizeY + v.SizeZ * v.SizeZ) + 1;
        foreach (var a in _aux) a.Frame(centre, width, diag);
    }

    /// <summary>An orthographic standard view (Top / Front / Right) sharing the main view's model.</summary>
    private sealed class AuxView
    {
        private readonly OrthographicCamera _cam = new() { NearPlaneDistance = 0.01, FarPlaneDistance = 1e6 };
        private readonly AmbientLight _amb = new(Colors.White);
        private readonly DirectionalLight _dir = new(Colors.Black, new Vector3D(0, 0, -1));
        private readonly Vector3D _look;
        private readonly Vector3D _up;
        private readonly Viewport3D _view;

        public AuxView(string name)
        {
            // View space: X = model x, Y = model z (up), Z = -model y.
            (_look, _up) = name switch
            {
                "TOP" => (new Vector3D(0, -1, 0), new Vector3D(0, 0, -1)),
                "RIGHT" => (new Vector3D(-1, 0, 0), new Vector3D(0, 1, 0)),
                _ => (new Vector3D(0, 0, -1), new Vector3D(0, 1, 0)), // FRONT
            };
            _cam.LookDirection = _look;
            _cam.UpDirection = _up;
            _dir.Direction = _look + new Vector3D(0.2, -0.3, 0.1);
            var lights = new Model3DGroup { Children = { _amb, _dir } };
            _view = new Viewport3D { Camera = _cam, IsHitTestVisible = false };
            _view.Children.Add(new ModelVisual3D { Content = lights });
            _view.Children.Add(Content);
            var label = new TextBlock { Text = name, FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(0x56, 0x60, 0x6C)), Margin = new Thickness(8, name == "TOP" ? 30 : 6, 0, 0), IsHitTestVisible = false };
            var triad = new AxisTriad { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(4, 0, 0, 4), Width = 70, Height = 70 };
            triad.SetOrientation(_look, _up);
            var grid = new Grid { Children = { _view, label, triad } };
            Host = new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(0xD5, 0xDA, 0xE0)), BorderThickness = new Thickness(0, 0, 1, 1), Child = grid, Visibility = Visibility.Collapsed };
        }

        public Border Host { get; }

        public ModelVisual3D Content { get; } = new();

        public void SetLighting(byte ambient, byte head)
        {
            _amb.Color = Color.FromRgb(ambient, ambient, ambient);
            byte d = (byte)Math.Min(255, head + 0x20 * (head > 0 ? 1 : 0));
            _dir.Color = Color.FromRgb(d, d, d);
        }

        public Size Size => new(_view.ActualWidth, _view.ActualHeight);

        /// <summary>Common framing: same centre and same width (= same scale, the cells are equal) in every view.</summary>
        public void Frame(Point3D centre, double width, double diag)
        {
            _cam.Width = width;
            _cam.Position = centre - 2 * diag * _look;
        }
    }

    // ------------------------------------------------------------------ camera (SolidWorks-style controls)

    /// <summary>Frames the model, keeping the current view orientation; <paramref name="visibleOnly"/> frames just the bodies shown.</summary>
    public void Fit(bool visibleOnly = false)
    {
        Point3D centre;
        double radius;
        if (visibleOnly && _content.Content is { } shown && !shown.Bounds.IsEmpty)
        {
            var vb3 = shown.Bounds;
            centre = new Point3D(vb3.X + vb3.SizeX / 2, vb3.Y + vb3.SizeY / 2, vb3.Z + vb3.SizeZ / 2);
            radius = 0.5 * Math.Sqrt(vb3.SizeX * vb3.SizeX + vb3.SizeY * vb3.SizeY + vb3.SizeZ * vb3.SizeZ);
        }
        else if (Die3D is not null)
        {
            var b = Bounds3D;
            centre = Map(b.X + b.SizeX / 2, b.Y + b.SizeY / 2, b.Z + b.SizeZ / 2);
            radius = 0.5 * Math.Sqrt(b.SizeX * b.SizeX + b.SizeY * b.SizeY + b.SizeZ * b.SizeZ);
        }
        else
        {
            var vb = ViewBounds;
            radius = Math.Max(vb.Right, vb.Height / 2);
            if (Die?.IsPlaneStrain == true) radius = Math.Max(radius, PartLength / 2);
            centre = new Point3D(0, vb.Top + vb.Height / 2, 0);
            radius *= 1.1;
        }

        _target = centre;
        double half = _camera.FieldOfView * Math.PI / 360;
        if (ActualWidth > 0 && ActualHeight > 0) half = Math.Atan(Math.Tan(half) * Math.Min(1, ActualHeight / ActualWidth));
        _distance = 1.05 * radius / Math.Sin(half);
        UpdateCamera();
        FitAux();
    }

    /// <summary>Sets a standard view direction (model z up) and refits.</summary>
    public void SetView(StandardView view)
    {
        // View space: X = model x, Y = model z (up), Z = −model y.
        (Vector3D look, Vector3D up) = view switch
        {
            StandardView.Front => (new Vector3D(0, 0, -1), new Vector3D(0, 1, 0)),
            StandardView.Back => (new Vector3D(0, 0, 1), new Vector3D(0, 1, 0)),
            StandardView.Left => (new Vector3D(1, 0, 0), new Vector3D(0, 1, 0)),
            StandardView.Right => (new Vector3D(-1, 0, 0), new Vector3D(0, 1, 0)),
            StandardView.Top => (new Vector3D(0, -1, 0), new Vector3D(0, 0, -1)),
            StandardView.Bottom => (new Vector3D(0, 1, 0), new Vector3D(0, 0, 1)),
            _ => (new Vector3D(-1, -0.8, -1.25), new Vector3D(0, 1, 0)),
        };
        look.Normalize();
        _look = look;
        _up = up - Vector3D.DotProduct(up, look) * look;
        _up.Normalize();
        Fit();
    }

    private Vector3D Right
    {
        get
        {
            var r = Vector3D.CrossProduct(_look, _up);
            r.Normalize();
            return r;
        }
    }

    private static Vector3D Rotated(Vector3D v, Vector3D axis, double degrees)
    {
        var m = Matrix3D.Identity;
        m.Rotate(new Quaternion(axis, degrees));
        return m.Transform(v);
    }

    /// <summary>Turntable-free trackball rotation: horizontal about the screen up axis, vertical about the screen right axis.</summary>
    private void Rotate(double degAboutUp, double degAboutRight)
    {
        var right = Right;
        _look = Rotated(_look, _up, degAboutUp);
        _look = Rotated(_look, right, degAboutRight);
        _up = Rotated(_up, right, degAboutRight);
        Orthonormalise();
    }

    private void Roll(double degrees)
    {
        _up = Rotated(_up, _look, degrees);
        Orthonormalise();
    }

    private void Orthonormalise()
    {
        _look.Normalize();
        _up -= Vector3D.DotProduct(_up, _look) * _look;
        _up.Normalize();
    }

    private void Pan(Vector screenDelta)
    {
        // One pixel moves the target by the size of a pixel at the target distance.
        double perPixel = 2 * _distance * Math.Tan(_camera.FieldOfView * Math.PI / 360) / Math.Max(ActualWidth, 1);
        _target += (-screenDelta.X * perPixel) * Right + (screenDelta.Y * perPixel) * _up;
    }

    /// <summary>Zooms by <paramref name="factor"/> (&lt; 1 = in) keeping the point under the cursor fixed.</summary>
    private void ZoomAt(Point cursor, double factor)
    {
        double w = Math.Max(ActualWidth, 1), h = Math.Max(ActualHeight, 1);
        double halfW = Math.Tan(_camera.FieldOfView * Math.PI / 360), halfH = halfW * h / w;
        var ray = _look + (2 * cursor.X / w - 1) * halfW * Right + (1 - 2 * cursor.Y / h) * halfH * _up;
        var pos = _target - _distance * _look;
        var p = pos + _distance * ray; // point under the cursor on the plane through the target
        _target = p + factor * (_target - p);
        _distance *= factor;
        UpdateCamera();
    }

    private void UpdateCamera()
    {
        _camera.Position = _target - _distance * _look;
        _camera.LookDirection = _distance * _look;
        _camera.UpDirection = _up;
        _camera.NearPlaneDistance = Math.Max(0.05, _distance / 2000);
        _headlight.Direction = _look - 0.35 * _up + 0.2 * Right;
        _triad.SetOrientation(_look, _up);
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        if (e.ClickCount == 2 && e.ChangedButton is MouseButton.Middle or MouseButton.Left)
        {
            Fit();
            return;
        }

        var mods = Keyboard.Modifiers;
        _drag = e.ChangedButton switch
        {
            MouseButton.Middle when mods.HasFlag(ModifierKeys.Control) => DragMode.Pan,
            MouseButton.Middle when mods.HasFlag(ModifierKeys.Shift) => DragMode.Zoom,
            MouseButton.Middle when mods.HasFlag(ModifierKeys.Alt) => DragMode.Roll,
            MouseButton.Middle => DragMode.Rotate,
            MouseButton.Left => DragMode.Rotate,
            MouseButton.Right => DragMode.Pan,
            _ => DragMode.None,
        };
        if (_drag == DragMode.None) return;
        _lastMouse = e.GetPosition(this);
        CaptureMouse();
        e.Handled = true;
    }

    private void EndDrag()
    {
        _drag = DragMode.None;
        ReleaseMouseCapture();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_drag == DragMode.None) return;
        var p = e.GetPosition(this);
        var d = p - _lastMouse;
        _lastMouse = p;
        switch (_drag)
        {
            case DragMode.Rotate:
                Rotate(-0.4 * d.X, -0.4 * d.Y);
                break;
            case DragMode.Pan:
                Pan(d);
                break;
            case DragMode.Zoom:
                _distance *= Math.Pow(1.01, d.Y); // drag down = zoom out, up = zoom in
                break;
            case DragMode.Roll:
                Roll(0.4 * d.X);
                break;
        }

        UpdateCamera();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        bool ctrl = mods.HasFlag(ModifierKeys.Control), shift = mods.HasFlag(ModifierKeys.Shift), alt = mods.HasFlag(ModifierKeys.Alt);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        int digit = key is >= Key.D1 and <= Key.D7 ? key - Key.D1 + 1 : key is >= Key.NumPad1 and <= Key.NumPad7 ? key - Key.NumPad1 + 1 : 0;
        Vector3D? arrow = key switch
        {
            Key.Left => new Vector3D(-1, 0, 0),
            Key.Right => new Vector3D(1, 0, 0),
            Key.Up => new Vector3D(0, -1, 0),
            Key.Down => new Vector3D(0, 1, 0),
            _ => null,
        };

        if (ctrl && digit > 0) SetView((StandardView)digit);
        else if (key == Key.F && !ctrl && !alt) Fit(visibleOnly: true);
        else if (key == Key.Z && !ctrl && !alt) ZoomAt(new Point(ActualWidth / 2, ActualHeight / 2), shift ? 1 / 1.25 : 1.25);
        else if (key == Key.Space) ShowViewMenu();
        else if (arrow is { } a)
        {
            if (ctrl) Pan(new Vector(-a.X * 0.05 * ActualWidth, -a.Y * 0.05 * ActualHeight));
            else if (alt && a.X != 0) Roll(15 * a.X);
            else
            {
                double step = shift ? 90 : 15;
                Rotate(-step * a.X, -step * a.Y);
            }

            UpdateCamera();
        }
        else
        {
            return;
        }

        e.Handled = true;
    }

    /// <summary>View orientation menu (SolidWorks: Spacebar).</summary>
    private void ShowViewMenu()
    {
        var menu = new ContextMenu { PlacementTarget = this, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        foreach (StandardView v in Enum.GetValues<StandardView>())
        {
            var item = new MenuItem { Header = v.ToString(), InputGestureText = $"Ctrl+{(int)v}" };
            item.Click += (_, _) => SetView(v);
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        var fit = new MenuItem { Header = "Zoom to fit", InputGestureText = "F" };
        fit.Click += (_, _) => Fit();
        menu.Items.Add(fit);
        menu.IsOpen = true;
    }

    // ------------------------------------------------------------------ geometry

    private bool _rebuildPending;

    /// <summary>
    /// Queues one rebuild after the current batch of property changes (bindings update several properties per view-model
    /// change, e.g. the snapshot and its grain field; rebuilding on each would draw half-updated, mismatched inputs).
    /// </summary>
    private void Rebuild()
    {
        if (_rebuildPending) return;
        _rebuildPending = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, () =>
        {
            _rebuildPending = false;
            try
            {
                RebuildNow();
            }
            catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidOperationException or NullReferenceException)
            {
                App.LogError(ex); // a drawing fault must not stop the view from updating with the next change
            }
        });
    }

    private void RebuildNow()
    {
        if (!IsVisible) return; // rebuilt when shown
        if (Die3D is { } d3)
        {
            Build3D(d3);
            return;
        }

        var group = new Model3DGroup();
        var die = Die;
        bool plane = die?.IsPlaneStrain == true;
        bool cut = CutAway && !plane;
        // Cut-away of a revolved 2D result: a 90° wedge is removed, facing the viewer for the default section axis (Y);
        // the X axis turns it a quarter turn, Flip a half turn.
        double phase = (CutAxis == CutPlaneAxis.X ? -Math.PI / 2 : 0) + (CutFlip ? Math.PI : 0);
        double a0 = cut ? phase + Math.PI / 4 : 0, a1 = cut ? phase + 2 * Math.PI - Math.PI / 4 : 2 * Math.PI;
        int segs = cut ? FullSegments * 3 / 4 : FullSegments;

        var snap = Snapshot as StepSnapshot;
        if (snap is not null && PartVisible)
        {
            var mesh = new MeshGeometry3D();
            bool field = ShowField;
            double[] t = field ? Normalised(snap) : new double[snap.Mesh.NodeCount];
            var te = field && Averaging == FieldAveraging.Element ? ContourPalette.ElementFraction(snap, Field, RangeMin, RangeMax) : null;
            if (plane) BuildExtrudedPart(mesh, snap, t, PartLength, te);
            else BuildRevolvedPart(mesh, snap, t, a0, a1, segs, cut, te);
            mesh.Freeze();
            var mat = Translucent(field ? _fieldMaterial : _plainMaterial, PartOpacity);
            var m = new GeometryModel3D(mesh, mat) { BackMaterial = mat };
            m.Freeze();
            group.Children.Add(m);
            if (GrainFlow && FlowLines is { Count: > 0 } flow && (plane || cut))
            {
                var b = mesh.Bounds;
                double diag = Math.Sqrt(b.SizeX * b.SizeX + b.SizeY * b.SizeY + b.SizeZ * b.SizeZ);
                group.Children.Add(EdgeTubes(ProfileFlowLines(flow, plane, a0, a1, PartLength, 2e-3 * diag), 6e-4 * diag, default, null));
            }
        }

        if (die is not null && ShowDies)
        {
            double gap = snap?.DieGap ?? 0;
            double ext = die.ViewExtent;
            const double block = 10; // die block thickness behind the deepest impression point, mm
            double baseLow = die.Bottom.HeightRange(0, ext).Min - block;
            double baseHigh = die.TopClosed.HeightRange(0, ext).Max + gap + block;
            var edgeSegs = new List<Point3D>();
            var dieModels = new List<GeometryModel3D>();
            foreach (var (surf, off, bas, visible, opacity) in new[]
            {
                (die.Bottom, 0.0, baseLow, BottomDieVisible, BottomDieOpacity),
                (die.TopClosed, gap, baseHigh, TopDieVisible, TopDieOpacity),
            })
            {
                if (!visible) continue;
                var prof = new List<Point>();
                foreach (var p in surf.Points)
                {
                    if (p.X <= ext) prof.Add(new Point(p.X, p.Y + off));
                }

                prof.Add(new Point(ext, surf.HeightAt(ext) + off));
                prof.Add(new Point(ext, bas));
                prof.Add(new Point(0, bas));
                var dm = new MeshGeometry3D();
                if (plane) ExtrudeProfile(dm, prof, PartLength);
                else RevolveProfile(dm, prof, a0, a1, segs);
                if (ShowDieEdges) ProfileEdges(edgeSegs, prof, plane, a0, a1, segs, PartLength);
                dm.Freeze();
                byte alpha = (byte)Math.Clamp(255 * opacity, 10, 255);
                var dieMat = Frozen(new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(alpha, DieColor.R, DieColor.G, DieColor.B))));
                var dmodel = new GeometryModel3D(dm, dieMat) { BackMaterial = dieMat };
                dmodel.Freeze();
                dieModels.Add(dmodel);
            }

            if (edgeSegs.Count > 0)
            {
                var vb = ViewBounds; // the 2D section frame: half-width (radius) by height
                group.Children.Add(EdgeTubes(edgeSegs, 1.1e-3 * Math.Sqrt(4 * vb.Width * vb.Width + vb.Height * vb.Height), default, null));
            }

            foreach (var dmodel in dieModels) group.Children.Add(dmodel); // translucent dies last, as in the 3D view
        }

        group.Freeze();
        SetContent(group);
        _bar.RangeMin = RangeMin;
        _bar.RangeMax = RangeMax;
        _bar.Visibility = ShowField && snap is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ true 3D results

    /// <summary>Model (x, y, z; z = press axis) → view space (X, Y up, Z), a proper rotation.</summary>
    private static Point3D Map(double x, double y, double z) => new(x, z, -y);

    private static Vector3D MapN(double x, double y, double z) => new(x, z, -y);

    // ------------------------------------------------------------------ view settings (live)

    public static readonly DependencyProperty PartVisibleProperty = Reg(nameof(PartVisible), typeof(bool), true, false);
    public static readonly DependencyProperty PartOpacityProperty = Reg(nameof(PartOpacity), typeof(double), 1.0, false);
    public static readonly DependencyProperty BottomDieVisibleProperty = Reg(nameof(BottomDieVisible), typeof(bool), true, false);
    public static readonly DependencyProperty TopDieVisibleProperty = Reg(nameof(TopDieVisible), typeof(bool), true, false);
    public static readonly DependencyProperty BottomDieOpacityProperty = Reg(nameof(BottomDieOpacity), typeof(double), 1.0, false);
    public static readonly DependencyProperty TopDieOpacityProperty = Reg(nameof(TopDieOpacity), typeof(double), 0.15, false);
    public static readonly DependencyProperty QuadViewProperty = DependencyProperty.Register(nameof(QuadView), typeof(bool), typeof(SolidViewport3D),
        new PropertyMetadata(false, (d, _) => ((SolidViewport3D)d).LayoutViews()));
    public static readonly DependencyProperty CutAxisProperty = Reg(nameof(CutAxis), typeof(CutPlaneAxis), CutPlaneAxis.Y, false);
    public static readonly DependencyProperty CutFlipProperty = Reg(nameof(CutFlip), typeof(bool), false, false);
    public static readonly DependencyProperty CutOffsetProperty = Reg(nameof(CutOffset), typeof(double), 0.0, false);
    public static readonly DependencyProperty ArrowSpacingProperty = Reg(nameof(ArrowSpacing), typeof(double), 1.6, false);
    public static readonly DependencyProperty ArrowScaleProperty = Reg(nameof(ArrowScale), typeof(double), 1.0, false);
    public static readonly DependencyProperty ClipLowProperty = DependencyProperty.Register(nameof(ClipLow), typeof(double), typeof(SolidViewport3D),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((SolidViewport3D)d).Rebuild()));
    public static readonly DependencyProperty ClipHighProperty = DependencyProperty.Register(nameof(ClipHigh), typeof(double), typeof(SolidViewport3D),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((SolidViewport3D)d).Rebuild()));

    /// <summary>Iso-clip: lower end of the visible range (fraction of the colour range); set by dragging the legend.</summary>
    public double ClipLow { get => (double)GetValue(ClipLowProperty); set => SetValue(ClipLowProperty, value); }

    /// <summary>Iso-clip: upper end of the visible range (fraction of the colour range).</summary>
    public double ClipHigh { get => (double)GetValue(ClipHighProperty); set => SetValue(ClipHighProperty, value); }

    public bool PartVisible { get => (bool)GetValue(PartVisibleProperty); set => SetValue(PartVisibleProperty, value); }

    /// <summary>Workpiece opacity, 0.05 … 1.</summary>
    public double PartOpacity { get => (double)GetValue(PartOpacityProperty); set => SetValue(PartOpacityProperty, value); }

    public bool BottomDieVisible { get => (bool)GetValue(BottomDieVisibleProperty); set => SetValue(BottomDieVisibleProperty, value); }

    public bool TopDieVisible { get => (bool)GetValue(TopDieVisibleProperty); set => SetValue(TopDieVisibleProperty, value); }

    /// <summary>Bottom die opacity, 0.05 … 1 (opaque by default).</summary>
    public double BottomDieOpacity { get => (double)GetValue(BottomDieOpacityProperty); set => SetValue(BottomDieOpacityProperty, value); }

    /// <summary>Top die opacity, 0.05 … 1 (15 % by default, so the workpiece shows through).</summary>
    public double TopDieOpacity { get => (double)GetValue(TopDieOpacityProperty); set => SetValue(TopDieOpacityProperty, value); }

    /// <summary>Four views: Top (top-left), Isometric (top-right, interactive), Front (bottom-left), Right (bottom-right).</summary>
    public bool QuadView { get => (bool)GetValue(QuadViewProperty); set => SetValue(QuadViewProperty, value); }

    /// <summary>Cut-away plane normal: model x or y (vertical planes through the press axis).</summary>
    public CutPlaneAxis CutAxis { get => (CutPlaneAxis)GetValue(CutAxisProperty); set => SetValue(CutAxisProperty, value); }

    /// <summary>Remove the other side of the cut plane.</summary>
    public bool CutFlip { get => (bool)GetValue(CutFlipProperty); set => SetValue(CutFlipProperty, value); }

    /// <summary>Cut plane position as a fraction (−1 … 1) of the half-extent of the model along the cut axis.</summary>
    public double CutOffset { get => (double)GetValue(CutOffsetProperty); set => SetValue(CutOffsetProperty, value); }

    /// <summary>Flow-arrow spacing in element sizes (smaller = denser).</summary>
    public double ArrowSpacing { get => (double)GetValue(ArrowSpacingProperty); set => SetValue(ArrowSpacingProperty, value); }

    /// <summary>Flow-arrow length / thickness multiplier.</summary>
    public double ArrowScale { get => (double)GetValue(ArrowScaleProperty); set => SetValue(ArrowScaleProperty, value); }

    /// <summary>Cut-away test in model coordinates: true = keep.</summary>
    private Func<double, double, bool> KeepTest()
    {
        if (!CutAway) return (_, _) => true;
        var b = Bounds3D;
        bool onX = CutAxis == CutPlaneAxis.X;
        // Bounds3D is in model coordinates (x, y, z).
        double center = onX ? b.X + b.SizeX / 2 : b.Y + b.SizeY / 2, half = onX ? b.SizeX / 2 : b.SizeY / 2;
        double at = center + Math.Clamp(CutOffset, -1, 1) * half;
        double side = CutFlip ? -1 : 1;
        return onX ? (x, _) => side * (x - at) >= 0 : (_, y) => side * (y - at) >= 0;
    }

    private static Material Translucent(Material m, double opacity) => opacity >= 0.999 ? m : Frozen(new DiffuseMaterial(
        m is DiffuseMaterial { Brush: ImageBrush ib } ? Frozen(new ImageBrush(ib.ImageSource) { ViewportUnits = ib.ViewportUnits, Viewport = ib.Viewport, TileMode = ib.TileMode, Opacity = opacity })
        : new SolidColorBrush(Color.FromArgb((byte)(255 * opacity), PlainColor.R, PlainColor.G, PlainColor.B))));

    private void Build3D(DieSet3D die)
    {
        var group = new Model3DGroup();
        (int Sx, int Sy)[] copies = die.Symmetry switch
        {
            Symmetry3D.Quarter => [(1, 1), (-1, 1), (1, -1), (-1, -1)],
            Symmetry3D.Half => [(1, 1), (1, -1)],
            _ => [(1, 1)],
        };
        var keep = KeepTest();
        bool field = ShowField;
        double vmax = 0;
        if (Snapshot is Snapshot3D s)
        {
            bool arrows = ShowVelocity && s.HasVelocity;
            if (arrows)
            {
                // Opaque arrows first, then the (translucent) part over them.
                var am = VelocityArrows(s, copies, keep, out vmax);
                var arrowModel = new GeometryModel3D(am, _fieldMaterial) { BackMaterial = _fieldMaterial };
                arrowModel.Freeze();
                group.Children.Add(arrowModel);
            }

            if (PartVisible)
            {
                var t = field && !arrows ? Normalised(s) : new double[s.Mesh.NodeCount];
                Func<int, bool>? visible = null;
                if (field && !arrows && (ClipLow > 0 || ClipHigh < 1) && RangeMax > RangeMin)
                {
                    var vals = s.Field(Field);
                    double a0 = RangeMin, span = RangeMax - RangeMin, lo = ClipLow, hi = ClipHigh;
                    visible = e => (vals[e] - a0) / span >= lo - 1e-9 && (vals[e] - a0) / span <= hi + 1e-9;
                }

                // Grain flow on a section: the part is cut exactly on the plane (flat cap) and the fibre lines are drawn on it.
                bool grainCut = GrainFlow && CutAway && !arrows;
                var te = field && !arrows && Averaging == FieldAveraging.Element ? ContourPalette.ElementFraction(s, Field, RangeMin, RangeMax) : null;
                var g = grainCut ? BuildPartFlatCut(s, copies, KeepPlane()) : BuildPart(s, t, copies, keep, visible, te);
                double opacity = arrows ? Math.Min(PartOpacity, 0.25) : PartOpacity;
                var baseMat = arrows ? _ghostMaterial : field ? _fieldMaterial : _plainMaterial;
                var mat = arrows ? baseMat : Translucent(baseMat, opacity);
                if (ShowContact) AddContactOverlay(group, s, copies, keep);
                // The field must belong to this mesh (while bindings update, it can still be the previous increment's).
                if (grainCut && FlowField is { } gr && ReferenceEquals(gr.Mesh, s.Mesh) && gr.X0.Length == s.NodeCount)
                {
                    var b = Bounds3D;
                    double diag = Math.Sqrt(b.SizeX * b.SizeX + b.SizeY * b.SizeY + b.SizeZ * b.SizeZ);
                    var lines = SectionFlowLines(s, gr, copies, KeepPlane(), FlowFiber, FlowLineCount, 2e-3 * diag);
                    if (lines.Count > 0) group.Children.Add(EdgeTubes(lines, 6e-4 * diag, default, null));
                }

                var model = new GeometryModel3D(g, mat) { BackMaterial = mat };
                model.Freeze();
                group.Children.Add(model);
                if (ShowEdges && !arrows && !GrainFlow) group.Children.Add(EdgeOverlay(g)); // grain flow: never the mesh
            }
        }

        if (ShowDies)
        {
            double gap = Snapshot?.DieGap ?? (die.IsImported ? die.Place(0, 0).Gap : 0);
            foreach (var (surf, off, visible, opacity) in new[] { (die.Bottom, 0.0, BottomDieVisible, BottomDieOpacity), (die.TopClosed, gap, TopDieVisible, TopDieOpacity) })
            {
                if (!visible) continue;
                byte alpha = (byte)Math.Clamp(255 * opacity, 10, 255);
                var dieMat = Frozen(new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(alpha, DieColor.R, DieColor.G, DieColor.B))));
                var dm = new MeshGeometry3D();
                if (surf is HeightFieldDie hf) AddHeightField(dm, die, hf, off, keep);
                else if (surf is TriMeshDie tm) AddSolid(dm, tm.Solid, off, keep, CutAway ? KeepPlane() : null);
                if (ShowDieEdges)
                {
                    // Feature edges at offset 0 (cached per die), lifted with the die; model z = view Y, model y = −view Z.
                    var b = Bounds3D;
                    double r = 1.1e-3 * Math.Sqrt(b.SizeX * b.SizeX + b.SizeY * b.SizeY + b.SizeZ * b.SizeZ);
                    group.Children.Add(EdgeTubes(DieFeatureEdges(die, surf), r, new Vector3D(0, off, 0), p => keep(p.X, -p.Z)));
                }

                dm.Freeze();
                var dmodel = new GeometryModel3D(dm, dieMat) { BackMaterial = dieMat };
                dmodel.Freeze();
                group.Children.Add(dmodel);
            }
        }

        group.Freeze();
        SetContent(group);
        var units = ImpressionFEA.App.Infrastructure.Units.Current;
        if (vmax > 0)
        {
            _bar.RangeMin = 0;
            _bar.RangeMax = vmax;
            _bar.Formatter = v => units.L(v).ToString(units.LengthFormat, System.Globalization.CultureInfo.InvariantCulture);
            _title.Text = "Material velocity |v|  [" + units.Speed + "]";
        }
        else
        {
            _bar.RangeMin = RangeMin;
            _bar.RangeMax = RangeMax;
            var f = Field;
            _bar.Formatter = v => ContourPalette.Label(v, f);
            _title.Text = FieldTitle;
        }

        _bar.InvalidateVisual();
        _bar.Visibility = (field || vmax > 0) && Snapshot is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The cut plane as (axis is X, position, side) for clipping die facets exactly.</summary>
    private (bool OnX, double At, double Side) KeepPlane()
    {
        var b = Bounds3D;
        bool onX = CutAxis == CutPlaneAxis.X;
        double center = onX ? b.X + b.SizeX / 2 : b.Y + b.SizeY / 2, half = onX ? b.SizeX / 2 : b.SizeY / 2;
        return (onX, center + Math.Clamp(CutOffset, -1, 1) * half, CutFlip ? -1 : 1);
    }

    // ------------------------------------------------------------------ part surface

    private static readonly ConditionalWeakTable<TetMesh, int[]> Adjacency = new();

    /// <summary>For every tet face (opposite vertex q, outward order <see cref="FaceIdx"/>) the tet across it, or −1 on the boundary.</summary>
    private static int[] AdjacencyOf(TetMesh mesh) => Adjacency.GetValue(mesh, m =>
    {
        var t = m.Tets;
        var adj = new int[4 * m.ElementCount];
        Array.Fill(adj, -1);
        var open = new Dictionary<(int, int, int), int>(4 * m.ElementCount);
        for (int e = 0; e < m.ElementCount; e++)
        {
            for (int q = 0; q < 4; q++)
            {
                var key = FaceKey(t[4 * e + FaceIdx[3 * q]], t[4 * e + FaceIdx[3 * q + 1]], t[4 * e + FaceIdx[3 * q + 2]]);
                if (open.Remove(key, out int other))
                {
                    adj[4 * e + q] = other / 4;
                    adj[other] = e;
                }
                else
                {
                    open[key] = 4 * e + q;
                }
            }
        }

        return adj;
    });

    /// <summary>Outward faces of a positively oriented tet, by opposite vertex 0..3.</summary>
    private static readonly int[] FaceIdx = [1, 2, 3, 0, 3, 2, 0, 1, 3, 0, 2, 1];

    /// <summary>
    /// Visible surface of the full part: every symmetry copy of the modelled tets, clipped by the cut-away plane.
    /// Outer faces get smooth normals and the contour values; faces exposed by the cut (or a symmetry plane whose
    /// mirror tet was cut away) are flat sections. Mirrored copies reverse the winding. With <paramref name="te"/> (element
    /// mode) every face takes its tet's value instead of the nodal ones.
    /// </summary>
    private static MeshGeometry3D BuildPart(Snapshot3D s, double[] t, (int Sx, int Sy)[] copies, Func<double, double, bool> keep, Func<int, bool>? visible = null, double[]? te = null)
    {
        var mesh = s.Mesh;
        var tets = mesh.Tets;
        var adj = AdjacencyOf(mesh);
        int ne = mesh.ElementCount;
        var nrm = new Vector3D[mesh.NodeCount];
        var f = mesh.BoundaryFaces;
        for (int k = 0; k < mesh.FaceIsSymmetry.Length; k++)
        {
            if (mesh.FaceIsSymmetry[k]) continue;
            int a = f[3 * k], b = f[3 * k + 1], c = f[3 * k + 2];
            var fn = FaceNormal(s, a, b, c);
            nrm[a] += fn;
            nrm[b] += fn;
            nrm[c] += fn;
        }

        var cx = new double[ne];
        var cy = new double[ne];
        for (int e = 0; e < ne; e++)
        {
            cx[e] = 0.25 * (s.X[tets[4 * e]] + s.X[tets[4 * e + 1]] + s.X[tets[4 * e + 2]] + s.X[tets[4 * e + 3]]);
            cy[e] = 0.25 * (s.Y[tets[4 * e]] + s.Y[tets[4 * e + 1]] + s.Y[tets[4 * e + 2]] + s.Y[tets[4 * e + 3]]);
        }

        bool Kept(int e, int sx, int sy) => keep(sx * cx[e], sy * cy[e]) && (visible?.Invoke(e) ?? true);
        var g = new MeshGeometry3D();
        foreach (var (sx, sy) in copies)
        {
            bool flip = sx * sy < 0;
            for (int e = 0; e < ne; e++)
            {
                if (!Kept(e, sx, sy)) continue;
                for (int q = 0; q < 4; q++)
                {
                    int a = tets[4 * e + FaceIdx[3 * q]], b = tets[4 * e + FaceIdx[3 * q + 1]], c = tets[4 * e + FaceIdx[3 * q + 2]];
                    int nb = adj[4 * e + q];
                    bool section;
                    if (nb >= 0)
                    {
                        if (Kept(nb, sx, sy)) continue; // interior face
                        section = true;
                    }
                    else
                    {
                        bool onX = mesh.OnX0[a] && mesh.OnX0[b] && mesh.OnX0[c], onY = mesh.OnY0[a] && mesh.OnY0[b] && mesh.OnY0[c];
                        if (onX || onY)
                        {
                            // Symmetry plane: hidden while the mirror tet in the neighbouring copy is shown.
                            if (Kept(e, onX ? -sx : sx, onY ? -sy : sy)) continue;
                            section = true;
                        }
                        else
                        {
                            section = false;
                        }
                    }

                    if (flip) (b, c) = (c, b);
                    Vector3D? flat = null;
                    if (section)
                    {
                        var fn = Vector3D.CrossProduct(
                            new Vector3D(sx * (s.X[b] - s.X[a]), sy * (s.Y[b] - s.Y[a]), s.Z[b] - s.Z[a]),
                            new Vector3D(sx * (s.X[c] - s.X[a]), sy * (s.Y[c] - s.Y[a]), s.Z[c] - s.Z[a]));
                        flat = MapN(fn.X, fn.Y, fn.Z);
                    }

                    foreach (int n in new[] { a, b, c })
                    {
                        g.Positions.Add(Map(sx * s.X[n], sy * s.Y[n], s.Z[n]));
                        var v = flat ?? MapN(sx * nrm[n].X, sy * nrm[n].Y, nrm[n].Z);
                        if (v.Length > 0) v.Normalize();
                        g.Normals.Add(v);
                        g.TextureCoordinates.Add(new Point(te?[e] ?? t[n], 0.5));
                    }

                    int i0 = g.Positions.Count - 3;
                    AddTri(g, i0, i0 + 1, i0 + 2);
                }
            }
        }

        g.Freeze();
        return g;
    }

    private static (int, int, int) FaceKey(int a, int b, int c)
    {
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        if (a > b) (a, b) = (b, a);
        return (a, b, c);
    }

    private static Vector3D FaceNormal(Snapshot3D s, int a, int b, int c) => Vector3D.CrossProduct(
        new Vector3D(s.X[b] - s.X[a], s.Y[b] - s.Y[a], s.Z[b] - s.Z[a]),
        new Vector3D(s.X[c] - s.X[a], s.Y[c] - s.Y[a], s.Z[c] - s.Z[a]));

    // ------------------------------------------------------------------ overlays

    /// <summary>
    /// Cone-tipped arrows of the nodal velocity, one node per cell of a spatial grid (ArrowSpacing element sizes), length
    /// proportional to speed (× ArrowScale), colour = speed on the contour palette. Symmetric models are mirrored.
    /// </summary>
    private MeshGeometry3D VelocityArrows(Snapshot3D s, (int Sx, int Sy)[] copies, Func<double, double, bool> keep, out double vmax)
    {
        var mesh = s.Mesh;
        int nn = mesh.NodeCount;
        vmax = 0;
        for (int n = 0; n < nn; n++) vmax = Math.Max(vmax, Math.Sqrt(s.VelocityX[n] * s.VelocityX[n] + s.VelocityY[n] * s.VelocityY[n] + s.VelocityZ[n] * s.VelocityZ[n]));
        var g = new MeshGeometry3D();
        if (vmax <= 0) return g;

        var f = mesh.BoundaryFaces;
        double sum = 0;
        int cnt = 0;
        for (int k = 0; k < f.Length && cnt < 3000; k += 3, cnt++)
        {
            sum += Math.Sqrt(Sq(s.X[f[k]] - s.X[f[k + 1]]) + Sq(s.Y[f[k]] - s.Y[f[k + 1]]) + Sq(s.Z[f[k]] - s.Z[f[k + 1]]));
        }

        double h = cnt > 0 ? sum / cnt : 1, cell = Math.Clamp(ArrowSpacing, 0.5, 6) * h, scale = Math.Clamp(ArrowScale, 0.1, 5);
        double maxLen = 1.4 * 1.6 * h * scale, radius = 0.07 * 1.6 * h * Math.Sqrt(scale);
        var picked = new List<int>();
        var used = new HashSet<(int, int, int)>();
        for (int n = 0; n < nn; n++)
        {
            if (used.Add(((int)Math.Floor(s.X[n] / cell), (int)Math.Floor(s.Y[n] / cell), (int)Math.Floor(s.Z[n] / cell)))) picked.Add(n);
        }

        foreach (var (sx, sy) in copies)
        {
            foreach (int n in picked)
            {
                if (!keep(sx * s.X[n], sy * s.Y[n])) continue;
                if ((sx < 0 && s.X[n] == 0) || (sy < 0 && s.Y[n] == 0)) continue; // plane nodes once
                double vx = sx * s.VelocityX[n], vy = sy * s.VelocityY[n], vz = s.VelocityZ[n];
                double sp = Math.Sqrt(vx * vx + vy * vy + vz * vz);
                if (sp < 0.02 * vmax) continue;
                var d = MapN(vx / sp, vy / sp, vz / sp);
                AddArrow(g, Map(sx * s.X[n], sy * s.Y[n], s.Z[n]), d, Math.Max(0.25, sp / vmax) * maxLen, radius, Math.Clamp(sp / vmax, 0.001, 0.999));
            }
        }

        g.Freeze();
        return g;
    }

    private static double Sq(double v) => v * v;

    private static void AddArrow(MeshGeometry3D g, Point3D p, Vector3D d, double len, double r, double t)
    {
        var u = Math.Abs(d.Y) < 0.9 ? Vector3D.CrossProduct(d, new Vector3D(0, 1, 0)) : Vector3D.CrossProduct(d, new Vector3D(1, 0, 0));
        u.Normalize();
        var w = Vector3D.CrossProduct(d, u);
        const int Sides = 6;
        double shaft = 0.68 * len, headR = 2.4 * r;
        var tex = new Point(t, 0.5);
        var neck = p + shaft * d;
        var tip = p + len * d;
        int b0 = g.Positions.Count;
        for (int i = 0; i < Sides; i++)
        {
            double a = 2 * Math.PI * i / Sides;
            var radial = Math.Cos(a) * u + Math.Sin(a) * w;
            g.Positions.Add(p + r * radial);
            g.Positions.Add(neck + r * radial);
            g.Positions.Add(neck + headR * radial);
            g.Normals.Add(radial);
            g.Normals.Add(radial);
            g.Normals.Add(radial + 0.4 * d);
            for (int k = 0; k < 3; k++) g.TextureCoordinates.Add(tex);
        }

        int tipIdx = g.Positions.Count;
        g.Positions.Add(tip);
        g.Normals.Add(d);
        g.TextureCoordinates.Add(tex);
        for (int i = 0; i < Sides; i++)
        {
            int i0 = b0 + 3 * i, i1 = b0 + 3 * ((i + 1) % Sides);
            AddTri(g, i0, i1, i1 + 1);         // shaft
            AddTri(g, i0, i1 + 1, i0 + 1);
            AddTri(g, i0 + 2, i1 + 2, tipIdx); // head cone
            AddTri(g, i0 + 1, i1 + 1, i1 + 2); // head base ring
            AddTri(g, i0 + 1, i1 + 2, i0 + 2);
        }
    }

    /// <summary>Surface faces whose three nodes touch a die, in that die's highlight colour, nudged outwards.</summary>
    private void AddContactOverlay(Model3DGroup group, Snapshot3D s, (int Sx, int Sy)[] copies, Func<double, double, bool> keep)
    {
        var mesh = s.Mesh;
        var f = mesh.BoundaryFaces;
        var b = Bounds3D;
        double eps = 8e-4 * Math.Sqrt(b.SizeX * b.SizeX + b.SizeY * b.SizeY + b.SizeZ * b.SizeZ);
        var meshes = new[] { new MeshGeometry3D(), new MeshGeometry3D() };
        foreach (var (sx, sy) in copies)
        {
            for (int k = 0; k < mesh.FaceIsSymmetry.Length; k++)
            {
                if (mesh.FaceIsSymmetry[k]) continue;
                int a = f[3 * k], bb = f[3 * k + 1], c = f[3 * k + 2];
                var ca = s.Contact[a];
                if (ca == NodeContact.Free || s.Contact[bb] != ca || s.Contact[c] != ca) continue;
                if (!keep(sx * (s.X[a] + s.X[bb] + s.X[c]) / 3, sy * (s.Y[a] + s.Y[bb] + s.Y[c]) / 3)) continue;
                var target = meshes[ca == NodeContact.BottomDie ? 0 : 1];
                var pa = Map(sx * s.X[a], sy * s.Y[a], s.Z[a]);
                var pb = Map(sx * s.X[bb], sy * s.Y[bb], s.Z[bb]);
                var pc = Map(sx * s.X[c], sy * s.Y[c], s.Z[c]);
                var n = Vector3D.CrossProduct(pb - pa, pc - pa);
                if (n.Length == 0) continue;
                n.Normalize();
                if (sx * sy < 0) n = -n; // mirrored copies reverse the winding
                int i0 = target.Positions.Count;
                foreach (var q in new[] { pa, pb, pc })
                {
                    target.Positions.Add(q + eps * n);
                    target.Normals.Add(n);
                }

                AddTri(target, i0, i0 + 1, i0 + 2);
            }
        }

        foreach (var (m, mat) in new[] { (meshes[0], _bottomContactMaterial), (meshes[1], _topContactMaterial) })
        {
            if (m.Positions.Count == 0) continue;
            m.Freeze();
            var gm = new GeometryModel3D(m, mat) { BackMaterial = mat };
            gm.Freeze();
            group.Children.Add(gm);
        }
    }

    // ------------------------------------------------------------------ dies

    private static readonly ConditionalWeakTable<IRigidDie, Point3D[]> DieEdgeCache = new();

    /// <summary>Sharp (feature) edges of an imported or height-field die at offset 0, view coordinates, as point pairs.</summary>
    private static Point3D[] DieFeatureEdges(DieSet3D die, IRigidDie surf) => DieEdgeCache.GetValue(surf, s =>
    {
        var dm = new MeshGeometry3D();
        if (s is HeightFieldDie hf) AddHeightField(dm, die, hf, 0, (_, _) => true);
        else if (s is TriMeshDie tm) AddSolid(dm, tm.Solid, 0, (_, _) => true, null);
        return FeatureEdges(dm, 30);
    });

    /// <summary>
    /// Edges of a triangle mesh (vertices welded by position) where the faces meet at more than
    /// <paramref name="angleDeg"/>, plus open boundaries: the creases of the original geometry, not the tessellation.
    /// </summary>
    private static Point3D[] FeatureEdges(MeshGeometry3D m, double angleDeg)
    {
        var pos = m.Positions;
        var idx = m.TriangleIndices;
        var b = m.Bounds;
        double tol = Math.Max(1e-6 * Math.Sqrt(b.SizeX * b.SizeX + b.SizeY * b.SizeY + b.SizeZ * b.SizeZ), 1e-9);
        var weld = new Dictionary<(long, long, long), int>();
        var wp = new List<Point3D>();
        var id = new int[pos.Count];
        for (int i = 0; i < pos.Count; i++)
        {
            var p = pos[i];
            var key = ((long)Math.Round(p.X / tol), (long)Math.Round(p.Y / tol), (long)Math.Round(p.Z / tol));
            if (!weld.TryGetValue(key, out int w))
            {
                w = wp.Count;
                weld[key] = w;
                wp.Add(p);
            }

            id[i] = w;
        }

        double cosLimit = Math.Cos(angleDeg * Math.PI / 180);
        var edges = new Dictionary<(int, int), (Vector3D N, int Count, bool Sharp)>();
        for (int k = 0; k + 2 < idx.Count; k += 3)
        {
            int a = id[idx[k]], c1 = id[idx[k + 1]], c2 = id[idx[k + 2]];
            if (a == c1 || c1 == c2 || a == c2) continue;
            var n = Vector3D.CrossProduct(wp[c1] - wp[a], wp[c2] - wp[a]);
            if (n.LengthSquared == 0) continue;
            n.Normalize();
            foreach (var (u, v) in new[] { (a, c1), (c1, c2), (c2, a) })
            {
                var key = u < v ? (u, v) : (v, u);
                if (edges.TryGetValue(key, out var e)) edges[key] = (e.N, e.Count + 1, e.Sharp || Vector3D.DotProduct(e.N, n) < cosLimit);
                else edges[key] = (n, 1, false);
            }
        }

        var segs = new List<Point3D>();
        foreach (var (key, e) in edges)
        {
            if (e.Count == 2 && !e.Sharp) continue;
            segs.Add(wp[key.Item1]);
            segs.Add(wp[key.Item2]);
        }

        return [.. segs];
    }

    /// <summary>
    /// Feature edges of a revolved (or mirrored and extruded) die profile, as point pairs: a circle (line) at every
    /// profile corner turning more than 30°, and the profile itself on the open ends (cut-away faces, extrusion ends).
    /// </summary>
    private static void ProfileEdges(List<Point3D> segsOut, List<Point> prof, bool plane, double a0, double a1, int segs, double length)
    {
        var p = plane ? prof.Select(q => new Point(-q.X, q.Y)).Reverse().Concat(prof).ToList() : prof;
        double cosLimit = Math.Cos(30 * Math.PI / 180);
        bool open = !plane && a1 - a0 < 2 * Math.PI - 1e-9;
        for (int k = 0; k < p.Count; k++)
        {
            bool corner = k == 0 || k == p.Count - 1;
            if (!corner)
            {
                var d1 = p[k] - p[k - 1];
                var d2 = p[k + 1] - p[k];
                if (d1.Length > 1e-9 && d2.Length > 1e-9) corner = d1 * d2 / (d1.Length * d2.Length) < cosLimit;
            }

            if (!corner) continue;
            if (plane)
            {
                segsOut.Add(new Point3D(p[k].X, p[k].Y, -length / 2));
                segsOut.Add(new Point3D(p[k].X, p[k].Y, length / 2));
            }
            else if (p[k].X > 1e-9)
            {
                for (int j = 0; j < segs; j++)
                {
                    double t0 = a0 + (a1 - a0) * j / segs, t1 = a0 + (a1 - a0) * (j + 1) / segs;
                    segsOut.Add(new Point3D(p[k].X * Math.Cos(t0), p[k].Y, p[k].X * Math.Sin(t0)));
                    segsOut.Add(new Point3D(p[k].X * Math.Cos(t1), p[k].Y, p[k].X * Math.Sin(t1)));
                }
            }
        }

        // Profile outlines on the open ends.
        var ends = plane ? new[] { -length / 2, length / 2 } : open ? new[] { a0, a1 } : [];
        foreach (double at in ends)
        {
            for (int k = 0; k + 1 < p.Count; k++)
            {
                if (!plane && p[k].X < 1e-9 && p[k + 1].X < 1e-9) continue;
                segsOut.Add(plane ? new Point3D(p[k].X, p[k].Y, at) : new Point3D(p[k].X * Math.Cos(at), p[k].Y, p[k].X * Math.Sin(at)));
                segsOut.Add(plane ? new Point3D(p[k + 1].X, p[k + 1].Y, at) : new Point3D(p[k + 1].X * Math.Cos(at), p[k + 1].Y, p[k + 1].X * Math.Sin(at)));
            }
        }
    }

    /// <summary>Thin square tubes of radius <paramref name="r"/> along point-pair segments (WPF 3D has no lines).</summary>
    private Model3D EdgeTubes(IReadOnlyList<Point3D> segs, double r, Vector3D shift, Func<Point3D, bool>? keep)
    {
        var g = new MeshGeometry3D();
        for (int i = 0; i + 1 < segs.Count; i += 2)
        {
            var p = segs[i] + shift;
            var q = segs[i + 1] + shift;
            if (keep is not null && !keep(p + 0.5 * (q - p))) continue;
            var d = q - p;
            if (d.LengthSquared < 1e-24) continue;
            d.Normalize();
            var u = Vector3D.CrossProduct(d, Math.Abs(d.Y) < 0.9 ? new Vector3D(0, 1, 0) : new Vector3D(1, 0, 0));
            u.Normalize();
            var v = Vector3D.CrossProduct(d, u);
            Vector3D[] c = [r * u, r * v, -r * u, -r * v];
            int b0 = g.Positions.Count;
            foreach (var o in c)
            {
                g.Positions.Add(p + o);
                g.Positions.Add(q + o);
            }

            for (int k = 0; k < 4; k++)
            {
                int a = b0 + 2 * k, bb = b0 + 2 * ((k + 1) % 4);
                AddTri(g, a, bb, a + 1);
                AddTri(g, a + 1, bb, bb + 1);
            }
        }

        g.Freeze();
        var m = new GeometryModel3D(g, _dieEdgeMaterial) { BackMaterial = _dieEdgeMaterial };
        m.Freeze();
        return m;
    }

    private static void AddHeightField(MeshGeometry3D dm, DieSet3D die, HeightFieldDie surf, double off, Func<double, double, bool> keep)
    {
        double step = Math.Max(surf.Dx, (die.XMax - die.XMin) / 150);
        int nx = (int)Math.Ceiling((die.XMax - die.XMin) / step), ny = (int)Math.Ceiling(2 * die.YMax / step);
        int baseIdx = dm.Positions.Count;
        for (int j = 0; j <= ny; j++)
        {
            for (int i = 0; i <= nx; i++)
            {
                double x = die.XMin + i * step, y = -die.YMax + j * step;
                double z = surf.HeightAt(x, y) + off;
                double hx = (surf.HeightAt(x + 0.5 * step, y) - surf.HeightAt(x - 0.5 * step, y)) / step;
                double hy = (surf.HeightAt(x, y + 0.5 * step) - surf.HeightAt(x, y - 0.5 * step)) / step;
                dm.Positions.Add(Map(x, y, z));
                var n = MapN(-hx, -hy, 1);
                n.Normalize();
                dm.Normals.Add(n);
            }
        }

        for (int j = 0; j < ny; j++)
        {
            for (int i = 0; i < nx; i++)
            {
                double cx = die.XMin + (i + 0.5) * step, cy = -die.YMax + (j + 0.5) * step;
                if (!keep(cx, cy)) continue;
                int p = baseIdx + j * (nx + 1) + i;
                AddTri(dm, p, p + 1, p + nx + 2);
                AddTri(dm, p, p + nx + 2, p + nx + 1);
            }
        }
    }

    /// <summary>Imported die solid with flat facets, raised by <paramref name="off"/>; facets are clipped exactly at the cut plane.</summary>
    private static void AddSolid(MeshGeometry3D dm, TriMeshSolid solid, double off, Func<double, double, bool> keep, (bool OnX, double At, double Side)? plane)
    {
        var v = solid.Vertices;
        var tri = solid.Triangles;
        for (int k = 0; k < tri.Length; k += 3)
        {
            var a = v[tri[k]];
            var b = v[tri[k + 1]];
            var c = v[tri[k + 2]];
            var n = (b - a).Cross(c - a);
            if (n.LengthSquared == 0) continue;
            var nv = MapN(n.X, n.Y, n.Z);
            nv.Normalize();
            var poly = plane is { } pl ? ClipToPlane(a, b, c, pl.OnX, pl.At, pl.Side) : [a, b, c];
            if (poly.Count < 3) continue;
            int i0 = dm.Positions.Count;
            foreach (var p in poly)
            {
                dm.Positions.Add(Map(p.X, p.Y, p.Z + off));
                dm.Normals.Add(nv);
            }

            for (int q = 1; q + 1 < poly.Count; q++) AddTri(dm, i0, i0 + q, i0 + q + 1);
        }
    }

    /// <summary>Part of triangle abc on the kept side of the plane (Sutherland–Hodgman), as a convex polygon.</summary>
    private static List<Vec3> ClipToPlane(Vec3 a, Vec3 b, Vec3 c, bool onX, double at, double side)
    {
        double D(Vec3 p) => side * ((onX ? p.X : p.Y) - at);
        var res = new List<Vec3>(4);
        Vec3[] tri = [a, b, c];
        for (int k = 0; k < 3; k++)
        {
            var p = tri[k];
            var q = tri[(k + 1) % 3];
            double dp = D(p), dq = D(q);
            if (dp >= 0) res.Add(p);
            if ((dp >= 0) != (dq >= 0)) res.Add(Vec3.Lerp(p, q, dp / (dp - dq)));
        }

        return res;
    }

    private double[] Normalised(Snapshot3D s)
    {
        var mesh = s.Mesh;
        var vals = s.Field(Field);
        var sum = new double[mesh.NodeCount];
        var w = new double[mesh.NodeCount];
        var t = mesh.Tets;
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            int a = t[4 * e], b = t[4 * e + 1], c = t[4 * e + 2], d = t[4 * e + 3];
            var ab = new Vector3D(s.X[b] - s.X[a], s.Y[b] - s.Y[a], s.Z[b] - s.Z[a]);
            var ac = new Vector3D(s.X[c] - s.X[a], s.Y[c] - s.Y[a], s.Z[c] - s.Z[a]);
            var ad = new Vector3D(s.X[d] - s.X[a], s.Y[d] - s.Y[a], s.Z[d] - s.Z[a]);
            double vol = Math.Abs(Vector3D.DotProduct(ab, Vector3D.CrossProduct(ac, ad))) / 6 + 1e-12;
            foreach (int n in new[] { a, b, c, d })
            {
                sum[n] += vol * vals[e];
                w[n] += vol;
            }
        }

        double lo = RangeMin, hi = RangeMax;
        double inv = hi > lo ? 1.0 / (hi - lo) : 0;
        for (int n = 0; n < sum.Length; n++) sum[n] = Math.Clamp(((w[n] > 0 ? sum[n] / w[n] : 0) - lo) * inv, 0.001, 0.999);
        return sum;
    }

    private double[] Normalised(StepSnapshot s)
    {
        var nodal = ContourPalette.NodalField(s, Field);
        double lo = RangeMin, hi = RangeMax;
        double inv = hi > lo ? 1.0 / (hi - lo) : 0;
        for (int i = 0; i < nodal.Length; i++) nodal[i] = Math.Clamp((nodal[i] - lo) * inv, 0.001, 0.999);
        return nodal;
    }

    /// <param name="te">Element mode: per-element colour fractions (outline segments take their owning element's value).</param>
    private static void BuildRevolvedPart(MeshGeometry3D mesh, StepSnapshot s, double[] t, double a0, double a1, int segs, bool caps, double[]? te = null)
    {
        var loop = s.Mesh.BoundaryLoop;
        var owner = s.Mesh.BoundaryEdgeOwner;
        int m = loop.Length;
        var p2 = loop.Select(n => new Point(s.X[n], s.Y[n])).ToArray();
        var tv = loop.Select(n => t[n]).ToArray();
        var nrm = SegmentNormals(p2);
        for (int k = 0; k < m; k++)
        {
            int k1 = (k + 1) % m;
            if (p2[k].X < 1e-9 && p2[k1].X < 1e-9) continue; // edge on the axis
            var na = VertexNormal(nrm, k, m);
            var nb = VertexNormal(nrm, k1, m, start: false);
            Revolve(mesh, p2[k], p2[k1], na, nb, te?[owner[k]] ?? tv[k], te?[owner[k]] ?? tv[k1], a0, a1, segs);
        }

        if (!caps) return;
        foreach (double ang in new[] { a0, a1 })
        {
            double c = Math.Cos(ang), sn = Math.Sin(ang);
            AddSectionFace(mesh, s, t, te, (x, y) => new Point3D(x * c, y, x * sn), ang == a0 ? new Vector3D(sn, 0, -c) : new Vector3D(-sn, 0, c));
        }
    }

    /// <param name="te">Element mode: per-element colour fractions (outline segments take their owning element's value).</param>
    private static void BuildExtrudedPart(MeshGeometry3D mesh, StepSnapshot s, double[] t, double length, double[]? te = null)
    {
        double z0 = -length / 2, z1 = length / 2;
        var loop = s.Mesh.BoundaryLoop;
        var owner = s.Mesh.BoundaryEdgeOwner;
        int m = loop.Length;
        foreach (double mir in new[] { 1.0, -1.0 })
        {
            var p2 = loop.Select(n => new Point(mir * s.X[n], s.Y[n])).ToArray();
            var nrm = SegmentNormals(p2);
            for (int k = 0; k < m; k++)
            {
                int k1 = (k + 1) % m;
                if (Math.Abs(p2[k].X) < 1e-9 && Math.Abs(p2[k1].X) < 1e-9) continue; // symmetry plane
                var na = VertexNormal(nrm, k, m);
                var nb = VertexNormal(nrm, k1, m, start: false);
                if (mir < 0) { na = -na; nb = -nb; }
                Extrude(mesh, p2[k], p2[k1], na, nb, te?[owner[k]] ?? t[loop[k]], te?[owner[k]] ?? t[loop[k1]], z0, z1);
            }

            foreach (double z in new[] { z0, z1 })
            {
                double mz = mir;
                AddSectionFace(mesh, s, t, te, (x, y) => new Point3D(mz * x, y, z), new Vector3D(0, 0, Math.Sign(z)));
            }
        }
    }

    /// <summary>
    /// The 2D section (every quad as two triangles) placed in 3D by <paramref name="place"/>: shared nodal vertices, or
    /// with <paramref name="te"/> four vertices per element carrying its own value (flat element colours).
    /// </summary>
    private static void AddSectionFace(MeshGeometry3D mesh, StepSnapshot s, double[] t, double[]? te, Func<double, double, Point3D> place, Vector3D normal)
    {
        var conn = s.Mesh.Connectivity;
        int baseIdx = mesh.Positions.Count;
        if (te is null)
        {
            for (int n = 0; n < s.Mesh.NodeCount; n++)
            {
                mesh.Positions.Add(place(s.X[n], s.Y[n]));
                mesh.Normals.Add(normal);
                mesh.TextureCoordinates.Add(new Point(t[n], 0.5));
            }

            for (int e = 0; e < s.Mesh.ElementCount; e++)
            {
                int i0 = baseIdx + conn[4 * e], i1 = baseIdx + conn[4 * e + 1], i2 = baseIdx + conn[4 * e + 2], i3 = baseIdx + conn[4 * e + 3];
                AddTri(mesh, i0, i1, i2);
                AddTri(mesh, i0, i2, i3);
            }

            return;
        }

        for (int e = 0; e < s.Mesh.ElementCount; e++)
        {
            int i0 = mesh.Positions.Count;
            for (int a = 0; a < 4; a++)
            {
                int n = conn[4 * e + a];
                mesh.Positions.Add(place(s.X[n], s.Y[n]));
                mesh.Normals.Add(normal);
                mesh.TextureCoordinates.Add(new Point(te[e], 0.5));
            }

            AddTri(mesh, i0, i0 + 1, i0 + 2);
            AddTri(mesh, i0, i0 + 2, i0 + 3);
        }
    }

    private static void RevolveProfile(MeshGeometry3D mesh, List<Point> prof, double a0, double a1, int segs)
    {
        var nrm = SegmentNormals(prof, closed: false);
        for (int k = 0; k < prof.Count - 1; k++)
        {
            if (prof[k].X < 1e-9 && prof[k + 1].X < 1e-9) continue;
            Revolve(mesh, prof[k], prof[k + 1], nrm[k], nrm[k], 0.5, 0.5, a0, a1, segs);
        }
    }

    private static void ExtrudeProfile(MeshGeometry3D mesh, List<Point> prof, double length)
    {
        var full = prof.Select(p => new Point(-p.X, p.Y)).Reverse().Concat(prof).ToList();
        var nrm = SegmentNormals(full, closed: false);
        for (int k = 0; k < full.Count - 1; k++)
            Extrude(mesh, full[k], full[k + 1], nrm[k], nrm[k], 0.5, 0.5, -length / 2, length / 2);
    }

    /// <summary>Outward (right-hand) normals of polyline segments in the section plane.</summary>
    private static Vector[] SegmentNormals(IReadOnlyList<Point> p, bool closed = true)
    {
        int m = p.Count;
        int count = closed ? m : m - 1;
        var n = new Vector[Math.Max(count, 1)];
        for (int k = 0; k < count; k++)
        {
            var d = p[(k + 1) % m] - p[k];
            var v = new Vector(d.Y, -d.X);
            if (v.Length > 0) v.Normalize();
            n[k] = v;
        }

        return n;
    }

    /// <summary>Smooth normal at a loop vertex unless the outline turns sharply there (keeps die-imprinted edges crisp).</summary>
    private static Vector VertexNormal(Vector[] seg, int k, int m, bool start = true)
    {
        var own = start ? seg[k] : seg[(k + m - 1) % m];
        var other = start ? seg[(k + m - 1) % m] : seg[k];
        if (own * other < Math.Cos(35 * Math.PI / 180)) return own;
        var v = own + other;
        if (v.Length > 0) v.Normalize();
        return v;
    }

    private static void Revolve(MeshGeometry3D mesh, Point a, Point b, Vector na, Vector nb, double ta, double tb, double a0, double a1, int segs)
    {
        int baseIdx = mesh.Positions.Count;
        for (int j = 0; j <= segs; j++)
        {
            double ang = a0 + (a1 - a0) * j / segs, c = Math.Cos(ang), s = Math.Sin(ang);
            mesh.Positions.Add(new Point3D(a.X * c, a.Y, a.X * s));
            mesh.Normals.Add(new Vector3D(na.X * c, na.Y, na.X * s));
            mesh.TextureCoordinates.Add(new Point(ta, 0.5));
            mesh.Positions.Add(new Point3D(b.X * c, b.Y, b.X * s));
            mesh.Normals.Add(new Vector3D(nb.X * c, nb.Y, nb.X * s));
            mesh.TextureCoordinates.Add(new Point(tb, 0.5));
        }

        for (int j = 0; j < segs; j++)
        {
            int i = baseIdx + 2 * j;
            AddTri(mesh, i, i + 2, i + 1);
            AddTri(mesh, i + 1, i + 2, i + 3);
        }
    }

    private static void Extrude(MeshGeometry3D mesh, Point a, Point b, Vector na, Vector nb, double ta, double tb, double z0, double z1)
    {
        int i = mesh.Positions.Count;
        foreach (double z in new[] { z0, z1 })
        {
            mesh.Positions.Add(new Point3D(a.X, a.Y, z));
            mesh.Normals.Add(new Vector3D(na.X, na.Y, 0));
            mesh.TextureCoordinates.Add(new Point(ta, 0.5));
            mesh.Positions.Add(new Point3D(b.X, b.Y, z));
            mesh.Normals.Add(new Vector3D(nb.X, nb.Y, 0));
            mesh.TextureCoordinates.Add(new Point(tb, 0.5));
        }

        AddTri(mesh, i, i + 1, i + 2);
        AddTri(mesh, i + 1, i + 3, i + 2);
    }

    private static void AddTri(MeshGeometry3D mesh, int a, int b, int c)
    {
        mesh.TriangleIndices.Add(a);
        mesh.TriangleIndices.Add(b);
        mesh.TriangleIndices.Add(c);
    }
}

/// <summary>Lighting of the 3D view.</summary>
public enum ViewLighting
{
    /// <summary>Ambient light only: no shading, colours exactly as on the colour bar.</summary>
    Uniform,

    /// <summary>Mostly ambient with a weak headlight.</summary>
    Soft,

    /// <summary>Headlight and fill light (strong shading).</summary>
    Shaded,
}

/// <summary>Normal of the cut-away plane (vertical planes through the press axis).</summary>
public enum CutPlaneAxis
{
    /// <summary>Plane y = const (front / back cut).</summary>
    Y,

    /// <summary>Plane x = const (left / right cut).</summary>
    X,
}
