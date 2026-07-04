using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Point = System.Windows.Point;
using Snapfield.App.ViewModels;

namespace Snapfield.App;

/// <summary>
/// Single main window with three tabs. Only the calibration canvas needs
/// code-behind (drag math + auto-save); the tabs and other content are pure MVVM.
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

        // Tray-resident: closing hides to the tray; real exit is the tray menu.
        Closing += (_, e) =>
        {
            if (App.Current.IsExiting) return;
            e.Cancel = true;
            Hide();
            App.Current.NotifyHiddenToTray();
        };
    }

    private void Surface_SizeChanged(object sender, SizeChangedEventArgs e) =>
        Cal.UpdateViewport(e.NewSize.Width, e.NewSize.Height);

    private void Monitor_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var el = (FrameworkElement)sender;
        if (el.DataContext is not MonitorViewModel monitor) return;
        if (!Cal.IsEditable) return; // receiver: layout is controller-owned, read-only
        _dragging = monitor;
        _dragStartCanvas = e.GetPosition(Surface);
        _dragStartXMm = monitor.XMm;
        _dragStartYMm = monitor.YMm;
        foreach (var m in Cal.Monitors) m.IsSelected = ReferenceEquals(m, monitor);
        el.CaptureMouse();
        e.Handled = true;
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
}
