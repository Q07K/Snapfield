using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;

namespace Snapfield.App;

/// <summary>Tiny code-only text prompt (device nickname, preset name, …).</summary>
public sealed class TextInputWindow : Window
{
    private readonly TextBox _input;

    /// <summary>The entered text (trimmed); may be empty when the user cleared it on purpose.</summary>
    public string? Value { get; private set; }

    public TextInputWindow(string title, string caption, string initial)
    {
        Title = title;
        Width = 340;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x22));

        _input = new TextBox { FontSize = 15, Padding = new Thickness(6, 4, 6, 4), Text = initial };
        var ok = new Button { Content = "확인", Padding = new Thickness(16, 5, 16, 5), IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "취소", Padding = new Thickness(16, 5, 16, 5), IsCancel = true };
        ok.Click += (_, _) => { Value = _input.Text.Trim(); DialogResult = true; };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = caption,
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xDD)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        });
        panel.Children.Add(_input);
        panel.Children.Add(buttons);
        Content = panel;

        Loaded += (_, _) => { _input.Focus(); _input.SelectAll(); };
    }
}
