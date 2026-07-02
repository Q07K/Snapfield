using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;

namespace Snapfield.App;

/// <summary>Tiny code-only dialog asking for a monitor's true diagonal in inches.</summary>
public sealed class SizeInputWindow : Window
{
    private readonly TextBox _input;

    public double? Inches { get; private set; }

    public SizeInputWindow(string currentLabel)
    {
        Title = "실제 화면 크기 보정";
        Width = 340;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x22));

        _input = new TextBox { FontSize = 16, Padding = new Thickness(6, 4, 6, 4) };
        var ok = new Button { Content = "적용", Padding = new Thickness(16, 5, 16, 5), IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "취소", Padding = new Thickness(16, 5, 16, 5), IsCancel = true };
        ok.Click += (_, _) =>
        {
            if (double.TryParse(_input.Text.Trim().TrimEnd('"'), out var v)) { Inches = v; DialogResult = true; }
            else _input.Focus();
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = $"이 모니터의 실제 대각선 크기(인치)를 입력하세요.\n현재 인식값: {currentLabel} — EDID가 잘못 보고하는 경우가 있습니다.",
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xDD)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        });
        panel.Children.Add(_input);
        panel.Children.Add(buttons);
        Content = panel;

        Loaded += (_, _) => _input.Focus();
    }
}
