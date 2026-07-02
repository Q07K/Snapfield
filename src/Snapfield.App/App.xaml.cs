using System.Windows;
using Snapfield.Platform.Monitors;

namespace Snapfield.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Must run before any window exists so Windows reports physical pixels.
        MonitorEnumerator.EnableDpiAwareness();
        base.OnStartup(e);
    }
}

