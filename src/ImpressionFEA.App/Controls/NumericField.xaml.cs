using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ImpressionFEA.App.Controls;

/// <summary>What a field's <see cref="NumericField.Minimum"/> / <see cref="NumericField.Maximum"/> measure (they are given in solver units).</summary>
public enum NumericQuantity
{
    /// <summary>Plain number: limits as given.</summary>
    Plain,

    /// <summary>Length or speed: limits in mm (mm/s), shown in the display length unit.</summary>
    Length,

    /// <summary>Temperature: limits in °C, shown in the display temperature unit.</summary>
    Temperature,

    /// <summary>Press load: limits in metric tonnes-force, shown in the display load unit.</summary>
    Load,
}

/// <summary>
/// Labelled numeric entry: commits on Enter / focus loss, reverts on Escape, flags unparsable text. A value outside
/// [<see cref="Minimum"/>, <see cref="Maximum"/>] is set to the nearest limit and flagged the same way.
/// </summary>
public partial class NumericField : UserControl
{
    public static readonly DependencyProperty MinimumProperty =
        DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(NumericField), new PropertyMetadata(double.NegativeInfinity, OnTipInputChanged));

    public static readonly DependencyProperty MaximumProperty =
        DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(NumericField), new PropertyMetadata(double.PositiveInfinity, OnTipInputChanged));

    public static readonly DependencyProperty QuantityProperty =
        DependencyProperty.Register(nameof(Quantity), typeof(NumericQuantity), typeof(NumericField), new PropertyMetadata(NumericQuantity.Plain, OnTipInputChanged));

    /// <summary>Smallest allowed value, in solver units (see <see cref="Quantity"/>).</summary>
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }

    /// <summary>Largest allowed value, in solver units (see <see cref="Quantity"/>).</summary>
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    /// <summary>Unit kind of the limits, so they follow the display unit system.</summary>
    public NumericQuantity Quantity { get => (NumericQuantity)GetValue(QuantityProperty); set => SetValue(QuantityProperty, value); }

    /// <summary>A limit converted to the display units of <see cref="Value"/>.</summary>
    private double Shown(double limit)
    {
        if (double.IsInfinity(limit)) return limit;
        var u = ImpressionFEA.App.Infrastructure.Units.Current;
        return Quantity switch
        {
            NumericQuantity.Length => u.L(limit),
            NumericQuantity.Temperature => u.T(limit),
            NumericQuantity.Load => u.LoadFromTonnes(limit),
            _ => limit,
        };
    }

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(NumericField),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((NumericField)d).ShowValue()));

    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(NumericField), new PropertyMetadata(""));

    public static readonly DependencyProperty UnitProperty =
        DependencyProperty.Register(nameof(Unit), typeof(string), typeof(NumericField), new PropertyMetadata("", OnTipInputChanged));

    public static readonly DependencyProperty FormatProperty = DependencyProperty.Register(
        nameof(Format), typeof(string), typeof(NumericField), new PropertyMetadata("F2", OnAutoChanged));

    public static readonly DependencyProperty IsReadOnlyProperty =
        DependencyProperty.Register(nameof(IsReadOnly), typeof(bool), typeof(NumericField), new PropertyMetadata(false));

    public static readonly DependencyProperty HintProperty =
        DependencyProperty.Register(nameof(Hint), typeof(string), typeof(NumericField), new PropertyMetadata("", OnTipInputChanged));

    public static readonly DependencyProperty AllowAutoProperty =
        DependencyProperty.Register(nameof(AllowAuto), typeof(bool), typeof(NumericField), new PropertyMetadata(false, OnAutoChanged));

    public static readonly DependencyProperty IsAutoProperty = DependencyProperty.Register(
        nameof(IsAuto), typeof(bool), typeof(NumericField),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnAutoChanged));

    private static void OnTipInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((NumericField)d).UpdateTip();

    private static void OnAutoChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var f = (NumericField)d;
        f.ShowValue();
        f.UpdateTip();
    }

    /// <summary>What the value means; shown in the tooltip above the allowed range.</summary>
    public string Hint { get => (string)GetValue(HintProperty); set => SetValue(HintProperty, value); }

    /// <summary>
    /// The value can be calculated automatically (<see cref="IsAuto"/>): grey while automatic, bold once typed over
    /// (an override). Clearing the box returns it to automatic.
    /// </summary>
    public bool AllowAuto { get => (bool)GetValue(AllowAutoProperty); set => SetValue(AllowAutoProperty, value); }

    /// <summary>The value is calculated automatically (only with <see cref="AllowAuto"/>).</summary>
    public bool IsAuto { get => (bool)GetValue(IsAutoProperty); set => SetValue(IsAutoProperty, value); }

    private readonly Brush _normalBorder;

    public NumericField()
    {
        InitializeComponent();
        _normalBorder = Box.BorderBrush;
        ShowValue();
        UpdateTip();
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string Unit
    {
        get => (string)GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    public string Format
    {
        get => (string)GetValue(FormatProperty);
        set => SetValue(FormatProperty, value);
    }

    public bool IsReadOnly
    {
        get => (bool)GetValue(IsReadOnlyProperty);
        set => SetValue(IsReadOnlyProperty, value);
    }

    private void ShowValue()
    {
        if (Box is null) return;
        Box.Text = _shownText = Value.ToString(Format, CultureInfo.InvariantCulture);
        Box.BorderBrush = _normalBorder;
        Box.ToolTip = null;
        Box.FontWeight = AllowAuto && !IsAuto ? FontWeights.Bold : FontWeights.Normal;
        Box.SetResourceReference(ForegroundProperty, AllowAuto && IsAuto ? "Muted" : "Ink");
    }

    private string _shownText = "";

    /// <summary>Tooltip: the hint, the automatic / override state and the allowed range in display units.</summary>
    private void UpdateTip()
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(Hint)) parts.Add(Hint);
        if (AllowAuto)
        {
            parts.Add(IsAuto
                ? "Automatic value (grey). Type a value to override it."
                : "Override (bold). Clear the box and press Enter to return to the automatic value.");
        }

        if (!double.IsInfinity(Minimum) || !double.IsInfinity(Maximum)) parts.Add("Allowed: " + Range(Shown(Minimum), Shown(Maximum)) + ".");
        ToolTip = parts.Count > 0 ? string.Join(Environment.NewLine, parts) : null;
    }

    private void Commit()
    {
        if (Box.Text == _shownText) return; // not edited: keep the value and its automatic / override state
        string text = Box.Text.Trim().Replace(',', '.');
        if (text.Length == 0 && AllowAuto)
        {
            IsAuto = true;
            ShowValue();
            return;
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v))
        {
            double lo = Shown(Minimum), hi = Shown(Maximum);
            double clamped = Math.Clamp(v, Math.Min(lo, hi), Math.Max(lo, hi));
            if (AllowAuto) IsAuto = false;
            Value = clamped;
            ShowValue();
            if (clamped != v)
            {
                // Out of range: keep the nearest allowed value and flag it like unparsable text.
                string limit = clamped.ToString(Format, CultureInfo.InvariantCulture) + (string.IsNullOrEmpty(Unit) || Unit == "–" ? "" : " " + Unit);
                Box.BorderBrush = (Brush)FindResource("Danger");
                Box.ToolTip = (clamped > v ? "Below the minimum: set to " : "Above the maximum: set to ") + limit
                    + " (allowed " + Range(lo, hi) + ").";
            }
        }
        else
        {
            Box.BorderBrush = (Brush)FindResource("Danger");
            Box.ToolTip = AllowAuto
                ? "Not a number — press Esc to restore the previous value, or clear the box for the automatic value."
                : "Not a number — press Esc to restore the previous value.";
        }
    }

    private string Range(double lo, double hi)
    {
        string F(double x) => double.IsInfinity(x) ? (x > 0 ? "∞" : "−∞") : x.ToString(Format, CultureInfo.InvariantCulture);
        return F(lo) + " – " + F(hi) + (string.IsNullOrEmpty(Unit) || Unit == "–" ? "" : " " + Unit);
    }

    private void Box_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => Commit();

    private void Box_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Commit();
            Box.SelectAll();
        }
        else if (e.Key == Key.Escape)
        {
            ShowValue();
        }
    }
}
