using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ImpressionFEA.Core.Cad;

namespace ImpressionFEA.App;

/// <summary>Asks which of the two outer bodies of an imported assembly is the moving top die.</summary>
public sealed class TopDieDialog : Window
{
    private readonly RadioButton _a;
    private readonly RadioButton _b;
    private readonly ForgingAssembly _asm;

    public TopDieDialog(ForgingAssembly asm)
    {
        ArgumentNullException.ThrowIfNull(asm);
        _asm = asm;
        Title = "Select the top (moving) die";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.White;

        var panel = new StackPanel { Margin = new Thickness(18), MaxWidth = 560 };
        panel.Children.Add(new TextBlock
        {
            Text = Invariant($"{asm.Name}: 3 bodies found. Body {asm.Workpiece.Index + 1} lies between the other two (centre of gravity {asm.OffAxisPercent:F1} % off their axis) and becomes the deforming workpiece; the outer bodies are rigid dies."),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });
        panel.Children.Add(new TextBlock { Text = "Which outer body is the top die that moves down?", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        _a = Option(asm.DieA);
        _b = Option(asm.DieB);
        (asm.SuggestedTop == asm.DieA ? _a : _b).IsChecked = true;
        panel.Children.Add(_a);
        panel.Children.Add(_b);
        panel.Children.Add(new TextBlock
        {
            Text = "The press axis runs through the two die centres of gravity. The top die travels along it until it touches the bottom die; the workpiece is first dropped onto the bottom die.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0x56, 0x60, 0x6C)),
            FontSize = 11,
            Margin = new Thickness(0, 10, 0, 14),
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "Import", IsDefault = true, MinWidth = 84, Padding = new Thickness(10, 4, 10, 4) };
        ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 84, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(8, 0, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Content = panel;
    }

    public CadBody SelectedTop => _a.IsChecked == true ? _asm.DieA : _asm.DieB;

    private RadioButton Option(CadBody b)
    {
        string hint = b == _asm.SuggestedTop ? $"  — higher along file {_asm.SuggestedAxisName}" : "";
        return new RadioButton
        {
            GroupName = "top",
            Margin = new Thickness(0, 3, 0, 3),
            Content = Invariant($"Body {b.Index + 1}: volume {b.Volume / 1000:F1} cm³, COG ({b.CenterOfGravity.X:F1}, {b.CenterOfGravity.Y:F1}, {b.CenterOfGravity.Z:F1}) mm{hint}"),
        };
    }

    private static string Invariant(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
