using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ImpressionFEA.App.Controls;

/// <summary>Labelled numeric entry: commits on Enter / focus loss, reverts on Escape, flags unparsable text.</summary>
public partial class NumericField : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(NumericField),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((NumericField)d).ShowValue()));

    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(NumericField), new PropertyMetadata(""));

    public static readonly DependencyProperty UnitProperty =
        DependencyProperty.Register(nameof(Unit), typeof(string), typeof(NumericField), new PropertyMetadata(""));

    public static readonly DependencyProperty FormatProperty = DependencyProperty.Register(
        nameof(Format), typeof(string), typeof(NumericField), new PropertyMetadata("F2", (d, _) => ((NumericField)d).ShowValue()));

    public static readonly DependencyProperty IsReadOnlyProperty =
        DependencyProperty.Register(nameof(IsReadOnly), typeof(bool), typeof(NumericField), new PropertyMetadata(false));

    private readonly Brush _normalBorder;

    public NumericField()
    {
        InitializeComponent();
        _normalBorder = Box.BorderBrush;
        ShowValue();
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
        Box.Text = Value.ToString(Format, CultureInfo.InvariantCulture);
        Box.BorderBrush = _normalBorder;
        Box.ToolTip = null;
    }

    private void Commit()
    {
        string text = Box.Text.Trim().Replace(',', '.');
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v))
        {
            Value = v;
            ShowValue();
        }
        else
        {
            Box.BorderBrush = (Brush)FindResource("Danger");
            Box.ToolTip = "Not a number — press Esc to restore the previous value.";
        }
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
