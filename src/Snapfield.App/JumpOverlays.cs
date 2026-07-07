using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Snapfield.App;

/// <summary>
/// Shared plumbing for the machine-switch overlay windows: borderless,
/// transparent, topmost, click-through, and never activated — they must not
/// steal focus from whatever the user is typing into, and a click must land
/// on the app underneath, not on the overlay.
/// </summary>
internal static class OverlayNative
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x20;
    private const long WS_EX_TOOLWINDOW = 0x80;
    private const long WS_EX_NOACTIVATE = 0x08000000;
    private const uint SWP_NOACTIVATE = 0x10;
    private const uint SWP_NOZORDER = 0x4;

    [DllImport("user32.dll")] private static extern long GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern long SetWindowLongPtr(IntPtr hWnd, int nIndex, long dwNewLong);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int nIndex);

    public const int SM_CXSCREEN = 0;
    public const int SM_CYSCREEN = 1;

    public static void MakeClickThrough(Window w)
    {
        var hwnd = new WindowInteropHelper(w).EnsureHandle();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE,
            GetWindowLongPtr(hwnd, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
    }

    /// <summary>Places the window at raw physical pixels, sidestepping WPF's
    /// DIP coordinate space (ambiguous across mixed-DPI monitors).</summary>
    public static void MoveWindowPx(Window w, int x, int y, int width, int height)
    {
        var hwnd = new WindowInteropHelper(w).EnsureHandle();
        SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, SWP_NOACTIVATE | SWP_NOZORDER);
    }

    public static Window Configure(Window w)
    {
        w.WindowStyle = WindowStyle.None;
        w.AllowsTransparency = true;
        w.Background = Brushes.Transparent;
        w.ResizeMode = ResizeMode.NoResize;
        w.ShowInTaskbar = false;
        w.ShowActivated = false;
        w.Topmost = true;
        w.Focusable = false;
        w.IsHitTestVisible = false;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = -4000; // parked offscreen until positioned in pixels
        w.Top = -4000;
        return w;
    }
}

/// <summary>
/// Landing pulse: two rings expanding from the point where a machine-switch
/// jump put the cursor, so the eye finds it instantly. Reused across jumps —
/// created once, repositioned and replayed per ping.
/// </summary>
public sealed class PingOverlayWindow : Window
{
    private const int SizePx = 280; // fixed pixel box; rings size in DIPs inside it

    private readonly Storyboard _pulse = new();

    public PingOverlayWindow()
    {
        OverlayNative.Configure(this);
        Width = SizePx; Height = SizePx; // placeholder; real size set in pixels

        var canvas = new Grid();
        canvas.Children.Add(MakeRing(Color.FromRgb(0x7F, 0x77, 0xDD), 4, beginMs: 0));
        canvas.Children.Add(MakeRing(Colors.White, 2.5, beginMs: 160));
        Content = canvas;

        _pulse.Completed += (_, _) => Hide();
    }

    private Ellipse MakeRing(Color color, double thickness, int beginMs)
    {
        var ring = new Ellipse
        {
            Stroke = new SolidColorBrush(color),
            StrokeThickness = thickness,
            Width = 10,
            Height = 10,
            Opacity = 0,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var grow = new DoubleAnimation(10, 110, TimeSpan.FromMilliseconds(520))
        { BeginTime = TimeSpan.FromMilliseconds(beginMs), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var fade = new DoubleAnimation(0.95, 0, TimeSpan.FromMilliseconds(520))
        { BeginTime = TimeSpan.FromMilliseconds(beginMs) };

        AddTo(_pulse, grow, ring, WidthProperty);
        AddTo(_pulse, grow.Clone(), ring, HeightProperty);
        AddTo(_pulse, fade, ring, OpacityProperty);
        return ring;
    }

    private static void AddTo(Storyboard sb, Timeline anim, DependencyObject target, DependencyProperty prop)
    {
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, new PropertyPath(prop));
        sb.Children.Add(anim);
    }

    /// <summary>Plays the pulse centred on a virtual-desktop pixel position.</summary>
    public void PlayAt(int px, int py)
    {
        OverlayNative.MakeClickThrough(this);
        OverlayNative.MoveWindowPx(this, px - SizePx / 2, py - SizePx / 2, SizePx, SizePx);
        if (!IsVisible) Show();
        _pulse.Begin(this, isControllable: true);
    }
}

/// <summary>
/// Machine-switcher strip (Alt+Tab style): shows every machine on the plane
/// left→right while Ctrl+Alt is held, highlighting where releasing the keys
/// will jump. Centred on the primary monitor of whichever machine draws it.
/// </summary>
public sealed class SwitcherOverlayWindow : Window
{
    private readonly StackPanel _items = new() { Orientation = Orientation.Horizontal };
    private readonly DispatcherTimer _safetyHide = new() { Interval = TimeSpan.FromSeconds(6) };

    public SwitcherOverlayWindow()
    {
        OverlayNative.Configure(this);
        SizeToContent = SizeToContent.WidthAndHeight;
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE2, 0x1F, 0x1F, 0x24)),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(8),
            Child = _items,
        };
        // If the committing key-release is ever lost (hook reinstalled mid-
        // session), the strip must not stay on screen forever.
        _safetyHide.Tick += (_, _) => HideStrip();
    }

    public void ShowSelection(string[] names, int selected)
    {
        _items.Children.Clear();
        for (var i = 0; i < names.Length; i++)
        {
            var on = i == selected;
            _items.Children.Add(new Border
            {
                Background = on ? new SolidColorBrush(Color.FromRgb(0x7F, 0x77, 0xDD)) : Brushes.Transparent,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(18, 9, 18, 9),
                Margin = new Thickness(2, 0, 2, 0),
                Child = new TextBlock
                {
                    Text = names[i],
                    FontSize = 15,
                    FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = on ? Brushes.White : new SolidColorBrush(Color.FromRgb(0xC9, 0xC9, 0xCE)),
                },
            });
        }

        OverlayNative.MakeClickThrough(this);
        if (!IsVisible) Show();
        UpdateLayout(); // measure the new content before converting to pixels

        var dpi = VisualTreeHelper.GetDpi(this);
        var w = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX);
        var h = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY);
        var sw = OverlayNative.GetSystemMetrics(OverlayNative.SM_CXSCREEN);
        var sh = OverlayNative.GetSystemMetrics(OverlayNative.SM_CYSCREEN);
        OverlayNative.MoveWindowPx(this, (sw - w) / 2, (int)(sh * 0.38), w, h);

        _safetyHide.Stop();
        _safetyHide.Start();
    }

    public void HideStrip()
    {
        _safetyHide.Stop();
        Hide();
    }
}
