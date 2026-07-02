using System.Windows;

namespace Snapfield.App;

public partial class NetworkWindow : Window
{
    public NetworkWindow()
    {
        InitializeComponent();
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        Title = $"Snapfield — Network  (v{v?.ToString(3)})";

        // The session lives at app level: closing this window is just closing a
        // view — the connection (and tray residency) keep running.
        DataContext = App.Current.Network;
    }
}
