using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Snapfield.App.ViewModels;

namespace Snapfield.App;

/// <summary>
/// Calibration window. Hosts a draggable canvas of monitors; the drag math lives
/// here (view concern), while all coordinate state lives in the view models.
/// </summary>
public partial class MainWindow : Window
{
    private CalibrationViewModel Vm => (CalibrationViewModel)DataContext;

    // Drag state, captured on mouse-down.
    private MonitorViewModel? _dragging;
    private Point _dragStartCanvas;
    private double _dragStartXMm, _dragStartYMm;

    public MainWindow()
    {
        InitializeComponent();
    }

    private EngineWindow? _engineWindow;

    private void Surface_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        Vm.UpdateViewport(e.NewSize.Width, e.NewSize.Height);
    }

    private void OpenEngine_Click(object sender, RoutedEventArgs e)
    {
        if (_engineWindow is null)
        {
            _engineWindow = new EngineWindow { Owner = this };
            _engineWindow.Closed += (_, _) => _engineWindow = null;
            _engineWindow.Show();
        }
        else
        {
            _engineWindow.Activate();
        }
    }

    private NetworkWindow? _networkWindow;

    private void OpenNetwork_Click(object sender, RoutedEventArgs e)
    {
        if (_networkWindow is null)
        {
            _networkWindow = new NetworkWindow { Owner = this };
            _networkWindow.Closed += (_, _) => _networkWindow = null;
            _networkWindow.Show();
        }
        else
        {
            _networkWindow.Activate();
        }
    }

    private void Monitor_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var border = (FrameworkElement)sender;
        if (border.DataContext is not MonitorViewModel monitor) return;

        _dragging = monitor;
        _dragStartCanvas = e.GetPosition(Surface);
        _dragStartXMm = monitor.XMm;
        _dragStartYMm = monitor.YMm;

        foreach (var m in Vm.Monitors) m.IsSelected = ReferenceEquals(m, monitor);

        border.CaptureMouse();
        e.Handled = true;
    }

    private void Monitor_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragging is null || e.LeftButton != MouseButtonState.Pressed) return;

        var now = e.GetPosition(Surface);
        var dxMm = (now.X - _dragStartCanvas.X) / Vm.Scale;
        var dyMm = (now.Y - _dragStartCanvas.Y) / Vm.Scale;

        _dragging.DragTo(_dragStartXMm + dxMm, _dragStartYMm + dyMm);
    }

    private void Monitor_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging is null) return;
        ((FrameworkElement)sender).ReleaseMouseCapture();
        _dragging = null;
        // Re-fit so the plane stays centred after a move.
        Vm.UpdateViewport(Surface.ActualWidth, Surface.ActualHeight);
    }
}
