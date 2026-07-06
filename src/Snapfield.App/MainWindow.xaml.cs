using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Point = System.Windows.Point;
using Snapfield.App.ViewModels;

namespace Snapfield.App;

/// <summary>
/// Single main window with three tabs. Code-behind covers the calibration canvas
/// (drag math + auto-save) and the ←/→ shortcuts on the role-select split;
/// the tabs and other content are pure MVVM.
/// </summary>
public partial class MainWindow : Window
{
    private CalibrationViewModel Cal => ((MainViewModel)DataContext).Calibration;

    private MonitorViewModel? _dragging;
    private Point _dragStartCanvas;
    private double _dragStartXMm, _dragStartYMm;

    public MainWindow()
    {
        InitializeComponent();
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        Title = $"Snapfield  (v{v?.ToString(3)})";

        _pinDigits = new[] { Pin0, Pin1, Pin2, Pin3, Pin4, Pin5 };
        _pinBoxes = new[] { PinB0, PinB1, PinB2, PinB3, PinB4, PinB5 };
        Loaded += (_, _) =>
        {
            if (DataContext is MainViewModel mv) mv.Network.PropertyChanged += Network_PropertyChanged;
            RefreshPinSlots();
        };

        // Tray-resident: closing hides to the tray; real exit is the tray menu.
        Closing += (_, e) =>
        {
            if (App.Current.IsExiting) return;
            e.Cancel = true;
            Hide();
            App.Current.NotifyHiddenToTray();
        };
    }

    /// <summary>←/→ pick a role on the full-bleed split (choose page only).</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled || Keyboard.Modifiers != ModifierKeys.None) return;
        if (DataContext is not MainViewModel vm || !vm.IsConnect || !vm.Network.IsChoosePage) return;
        if (e.Key == Key.Left) { vm.Network.ChooseControllerCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Right) { vm.Network.ChooseReceiverCommand.Execute(null); e.Handled = true; }
    }

    private void Surface_SizeChanged(object sender, SizeChangedEventArgs e) =>
        Cal.UpdateViewport(e.NewSize.Width, e.NewSize.Height);

    private void Monitor_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var el = (FrameworkElement)sender;
        if (el.DataContext is not MonitorViewModel monitor) return;
        Cal.Select(monitor); // opens the inspector — info shows on read-only planes too
        e.Handled = true;
        if (!Cal.IsEditable) return; // receiver: layout is controller-owned, no drag
        _dragging = monitor;
        _dragStartCanvas = e.GetPosition(Surface);
        _dragStartXMm = monitor.XMm;
        _dragStartYMm = monitor.YMm;
        el.CaptureMouse();
    }

    // ── Canvas zoom & pan ─────────────────────────────────────────────────────
    private bool _panning;
    private Point _panLast;
    private double _panDistPx;

    /// <summary>Wheel zooms around the cursor (works over monitors too).</summary>
    private void Canvas_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var p = e.GetPosition(Surface);
        Cal.ZoomAt(e.Delta > 0 ? 1.2 : 1 / 1.2, p.X, p.Y);
        e.Handled = true;
    }

    /// <summary>Empty-canvas press: drag pans (when zoomed), a plain click clears
    /// the selection — decided on release by how far the mouse travelled.</summary>
    private void Surface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _panning = true;
        _panDistPx = 0;
        _panLast = e.GetPosition(Surface);
        Surface.CaptureMouse();
    }

    private void Surface_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_panning) return;
        var p = e.GetPosition(Surface);
        _panDistPx += Math.Abs(p.X - _panLast.X) + Math.Abs(p.Y - _panLast.Y);
        Cal.PanBy(p.X - _panLast.X, p.Y - _panLast.Y);
        _panLast = p;
    }

    private void Surface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_panning) return;
        _panning = false;
        Surface.ReleaseMouseCapture();
        if (_panDistPx < 4) Cal.Select(null); // didn't move: it was a click
    }

    private void Monitor_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragging is null || e.LeftButton != MouseButtonState.Pressed) return;
        var now = e.GetPosition(Surface);
        _dragging.DragTo(_dragStartXMm + (now.X - _dragStartCanvas.X) / Cal.Scale,
                         _dragStartYMm + (now.Y - _dragStartCanvas.Y) / Cal.Scale);
    }

    private void Monitor_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging is null) return;
        ((FrameworkElement)sender).ReleaseMouseCapture();
        _dragging = null;
        Cal.UpdateViewport(Surface.ActualWidth, Surface.ActualHeight);
        Cal.AutoSave(); // persist the arrangement silently — no Save button needed
    }

    private void ToggleKind_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MonitorViewModel m) Cal.ToggleKind(m);
    }

    private void ResizeMonitor_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MonitorViewModel m) return;
        var dlg = new SizeInputWindow(m.DiagonalText) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Inches is double inches) Cal.ResizeMonitor(m, inches);
    }

    private void RemoveMonitor_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MonitorViewModel m) Cal.RemoveMonitor(m);
    }

    private void Toast_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ToastItem t)
            ((MainViewModel)DataContext).DismissToast(t);
    }

    // ── Pin boxes: one invisible TextBox drives six drawn digit boxes ─────────
    private readonly TextBlock[] _pinDigits;
    private readonly Border[] _pinBoxes;
    private static readonly Brush PinGreen = Frozen(0x5F, 0xCF, 0x8A); // matches the receiver's code
    private static readonly Brush PinBlue = Frozen(0x5B, 0x87, 0xEE);  // active box
    private static readonly Brush PinRed = Frozen(0xE0, 0x6A, 0x6A);   // auth failed
    private static readonly Brush PinIdle = Frozen(0x26, 0x28, 0x38);  // empty box
    private static readonly Effect PinGlow = FrozenGlow();

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Effect FrozenGlow()
    {
        var glow = new DropShadowEffect { Color = Color.FromRgb(0x5B, 0x87, 0xEE), BlurRadius = 12, ShadowDepth = 0, Opacity = 0.45 };
        glow.Freeze();
        return glow;
    }

    private NetworkViewModel? Net => (DataContext as MainViewModel)?.Network;

    private void Network_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(NetworkViewModel.PinError):
                RefreshPinSlots();
                if (Net?.PinError == true) // auth failed: put the user right back on the code
                    Dispatcher.BeginInvoke(() => { PinBox.Focus(); PinBox.SelectAll(); });
                break;
            case nameof(NetworkViewModel.ShowNewForm) when Net?.ShowNewForm == true:
                // Sheet opened: a discovered pick prefills the IP, so land on the code.
                Dispatcher.BeginInvoke(() =>
                {
                    if (string.IsNullOrWhiteSpace(Net?.RemoteHost)) HostBox.Focus(); else PinBox.Focus();
                });
                break;
        }
    }

    private void PinBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Digits only, max six — covers typing, IME leftovers, and any paste shape.
        var clean = new string(PinBox.Text.Where(char.IsDigit).ToArray());
        if (clean.Length > 6) clean = clean[..6];
        if (clean != PinBox.Text)
        {
            var pos = Math.Min(PinBox.SelectionStart, clean.Length);
            PinBox.Text = clean; // re-fires TextChanged with the clean value
            PinBox.SelectionStart = pos;
            return;
        }

        RefreshPinSlots();

        // Six digits typed by hand → connect without hunting for a button.
        // Recent/discovered paths set the pin programmatically and connect themselves.
        if (clean.Length == 6 && PinBox.IsKeyboardFocused &&
            Net is { } net && net.ConnectCommand.CanExecute(null))
            net.ConnectCommand.Execute(null);
    }

    private void PinBox_FocusChanged(object sender, RoutedEventArgs e) => RefreshPinSlots();

    private void RefreshPinSlots()
    {
        var text = PinBox.Text;
        var err = Net?.PinError == true;
        var focused = PinBox.IsKeyboardFocused;
        for (var i = 0; i < 6; i++)
        {
            var current = i == text.Length && focused && !err;
            _pinDigits[i].Text = i < text.Length ? text[i].ToString() : "";
            _pinDigits[i].Foreground = err ? PinRed : PinGreen;
            _pinBoxes[i].BorderBrush = err ? PinRed : current ? PinBlue : PinIdle;
            _pinBoxes[i].Effect = current ? PinGlow : null;
        }
    }
}
