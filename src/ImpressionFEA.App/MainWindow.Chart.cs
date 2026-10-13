using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using ImpressionFEA.App.Controls;
using ImpressionFEA.App.ViewModels;
using ImpressionFEA.Core.Simulation;
using OxyPlot;

namespace ImpressionFEA.App;

/// <summary>
/// Telemetry chart interaction (docked or popped out): hover shows the values under the mouse, press and hold shows a
/// card with the values and, for 2D runs, a thumbnail of the part at that increment; double-click loads that run at
/// that increment.
/// </summary>
public partial class MainWindow
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private Popup? _hoverTip;
    private Popup? _holdCard;
    private bool _holding;
    private readonly Dictionary<(RunRecord?, ForgingSnapshot), BitmapSource> _thumbs = [];

    /// <summary>Hooks a plot view up: OxyPlot's own left-button tracker is replaced by the hover tip and hold card.</summary>
    private void InitChartInteraction(OxyPlot.Wpf.PlotView view)
    {
        var controller = new PlotController();
        controller.UnbindMouseDown(OxyMouseButton.Left);
        view.Controller = controller;
        view.PreviewMouseLeftButtonDown += Chart_MouseDown;
        view.PreviewMouseLeftButtonUp += Chart_MouseUp;
        view.PreviewMouseMove += Chart_MouseMove;
        view.MouseLeave += Chart_MouseLeave;
    }

    private void DetachChartInteraction(OxyPlot.Wpf.PlotView view)
    {
        view.PreviewMouseLeftButtonDown -= Chart_MouseDown;
        view.PreviewMouseLeftButtonUp -= Chart_MouseUp;
        view.PreviewMouseMove -= Chart_MouseMove;
        view.MouseLeave -= Chart_MouseLeave;
        HideChartPopups();
    }

    private CurveHit? HitAt(FrameworkElement view, MouseEventArgs e, out Point p)
    {
        p = e.GetPosition(view);
        return Vm.HitCurve(new ScreenPoint(p.X, p.Y));
    }

    private void Chart_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var view = (FrameworkElement)sender;
        if (e.ClickCount == 2)
        {
            HideChartPopups();
            var p2 = e.GetPosition(view);
            if (Vm.PickCurve(new ScreenPoint(p2.X, p2.Y)) is { } run)
            {
                RunsList.ScrollIntoView(run);
                e.Handled = true;
            }

            return;
        }

        if (HitAt(view, e, out var p) is not { } hit) return;
        _holding = true;
        view.CaptureMouse();
        if (_hoverTip is not null) _hoverTip.IsOpen = false;
        ShowCard(view, p, hit);
        e.Handled = true;
    }

    private void Chart_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_holding) return;
        _holding = false;
        ((FrameworkElement)sender).ReleaseMouseCapture();
        if (_holdCard is not null) _holdCard.IsOpen = false;
    }

    private void Chart_MouseMove(object sender, MouseEventArgs e)
    {
        var view = (FrameworkElement)sender;
        var hit = HitAt(view, e, out var p);
        if (_holding)
        {
            if (hit is not null) ShowCard(view, p, hit);
            return;
        }

        if (hit is null || e.LeftButton == MouseButtonState.Pressed)
        {
            if (_hoverTip is not null) _hoverTip.IsOpen = false;
            return;
        }

        ShowHover(view, p, hit);
    }

    private void Chart_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_holding && _hoverTip is not null) _hoverTip.IsOpen = false;
    }

    private void HideChartPopups()
    {
        _holding = false;
        if (_hoverTip is not null) _hoverTip.IsOpen = false;
        if (_holdCard is not null) _holdCard.IsOpen = false;
    }

    // ------------------------------------------------------------------ popups

    private static Popup MakePopup() => new()
    {
        AllowsTransparency = true,
        Placement = PlacementMode.Relative,
        IsHitTestVisible = false,
        StaysOpen = true,
    };

    private static Border Frame(UIElement content) => new()
    {
        Background = Brushes.White,
        BorderBrush = new SolidColorBrush(Color.FromRgb(0xAE, 0xB5, 0xBF)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(9, 6, 9, 7),
        Margin = new Thickness(0, 0, 8, 8),
        Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 2, Opacity = 0.25 },
        Child = content,
    };

    private static UIElement CurveTitle(CurveHit hit, double maxWidth)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new Border
        {
            Width = 14, Height = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromRgb(hit.Color.R, hit.Color.G, hit.Color.B)),
        });
        row.Children.Add(new TextBlock { Text = hit.Name, FontWeight = System.Windows.FontWeights.SemiBold, MaxWidth = maxWidth, TextTrimming = TextTrimming.CharacterEllipsis });
        return row;
    }

    private static TextBlock Muted(string text) => new()
    {
        Text = text, FontSize = 10.5, Foreground = new SolidColorBrush(Color.FromRgb(0x56, 0x60, 0x6C)), Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap,
    };

    private static void Place(Popup popup, FrameworkElement view, Point p, double width)
    {
        popup.PlacementTarget = view;
        popup.HorizontalOffset = p.X + 16 + width > view.ActualWidth ? Math.Max(0, p.X - 16 - width) : p.X + 16;
        popup.VerticalOffset = p.Y + 14;
        popup.IsOpen = true;
    }

    /// <summary>Hover: the run, increment, die gap, load and fill under the mouse.</summary>
    private void ShowHover(FrameworkElement view, Point p, CurveHit hit)
    {
        var u = Infrastructure.Units.Current;
        var r = hit.Row;
        var s = new StackPanel();
        s.Children.Add(CurveTitle(hit, 260));
        s.Children.Add(new TextBlock
        {
            Text = string.Format(Inv, "Step {0} · gap {1}\n{2} · fill {3:F1} %", r.Step, u.Lf(r.DieGap), u.Loadf(r.LoadTonnes), r.FillPercent),
            FontFamily = (FontFamily)FindResource("Mono"), FontSize = 11, Margin = new Thickness(0, 3, 0, 0),
        });
        s.Children.Add(Muted("Hold to see the part · double-click to load this moment"));
        _hoverTip ??= MakePopup();
        _hoverTip.Child = Frame(s);
        Place(_hoverTip, view, p, 280);
    }

    /// <summary>Press and hold: a card with every value at that increment and, for 2D runs, a thumbnail of the part.</summary>
    private void ShowCard(FrameworkElement view, Point p, CurveHit hit)
    {
        _holdCard ??= MakePopup();
        _holdCard.Child = CardContent(hit);
        Place(_holdCard, view, p, 270);
    }

    /// <summary>The hold card: run, thumbnail (2D runs) and every value at that increment.</summary>
    internal Border CardContent(CurveHit hit)
    {
        var u = Infrastructure.Units.Current;
        var r = hit.Row;
        var s = new StackPanel { Width = 250 };
        s.Children.Add(CurveTitle(hit, 230));
        if (Thumbnail(hit) is { } img)
        {
            s.Children.Add(new Image { Source = img, Width = 250, Height = 250 * img.PixelHeight / img.PixelWidth, Margin = new Thickness(0, 6, 0, 2) });
        }

        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var rows = new (string, string)[]
        {
            ("Step", r.Step.ToString(Inv) + string.Format(Inv, "   t = {0:F3} s", r.Time)),
            ("Die gap", u.Lf(r.DieGap)),
            ("Ram stroke", u.Lf(r.Stroke)),
            ("Press load", u.Loadf(r.LoadTonnes)),
            ("Cavity fill", r.FillPercent.ToString("F1", Inv) + " %"),
            ("σ̄ max", u.Sf(r.MaxVonMises)),
            ("ε̄ max", r.MaxEffectiveStrain.ToString("F3", Inv)),
            ("Mesh", r.Elements.ToString("N0", Inv) + " elements"),
        };
        var mono = (FontFamily)FindResource("Mono");
        for (int i = 0; i < rows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition());
            var k = new TextBlock { Text = rows[i].Item1, Foreground = new SolidColorBrush(Color.FromRgb(0x56, 0x60, 0x6C)), Margin = new Thickness(0, 1, 10, 1), FontSize = 11 };
            var v = new TextBlock { Text = rows[i].Item2, FontFamily = mono, FontSize = 11, TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 1, 0, 1) };
            Grid.SetRow(k, i);
            Grid.SetRow(v, i);
            Grid.SetColumn(v, 1);
            grid.Children.Add(k);
            grid.Children.Add(v);
        }

        s.Children.Add(grid);
        string note = hit.Snapshot is null
            ? hit.Run is { CanShow: false } ? "Full results released to save memory: telemetry only." : "Double-click to load this moment."
            : hit.Snapshot is StepSnapshot ? "Double-click to load this moment." : "3D run: double-click to load this moment in the viewer.";
        s.Children.Add(Muted(note));
        return Frame(s);
    }

    /// <summary>The 2D part at that increment, coloured by the field selected in the results list (cached).</summary>
    private BitmapSource? Thumbnail(CurveHit hit)
    {
        if (hit.Snapshot is not StepSnapshot snap || hit.Die2D is null) return null;
        if (_thumbs.TryGetValue((hit.Run, snap), out var cached)) return cached;
        if (_thumbs.Count > 80) _thumbs.Clear();

        var field = Vm.SelectedField.GrainFlow ? ResultField.EffectiveStrain : Vm.SelectedField.Field;
        var values = snap.Field(field);
        double lo = values.Length > 0 ? values.Min() : 0, hi = values.Length > 0 ? values.Max() : 1;
        if (hi <= lo) hi = lo + 1;
        const int w = 500, h = 340;
        var v = new FieldViewport
        {
            Snapshot = snap, Die = hit.Die2D, Field = field, RangeMin = lo, RangeMax = hi, FieldTitle = Vm.SelectedField.GrainFlow ? "Effective strain ε̄" : Vm.SelectedField.Name,
            ShowMesh = false, Mirror = true, ViewBounds = hit.ViewBounds, Units = Vm.SelectedUnits, Averaging = Vm.Averaging,
        };
        try
        {
            v.Measure(new Size(w, h));
            v.Arrange(new Rect(0, 0, w, h));
            v.UpdateLayout();
            var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(v);
            bmp.Freeze();
            _thumbs[(hit.Run, snap)] = bmp;
            return bmp;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IndexOutOfRangeException)
        {
            App.LogError(ex); // a thumbnail must never break the chart
            return null;
        }
    }
}
