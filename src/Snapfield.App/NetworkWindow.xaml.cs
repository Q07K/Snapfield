using System.Windows;
using Snapfield.App.ViewModels;

namespace Snapfield.App;

public partial class NetworkWindow : Window
{
    public NetworkWindow()
    {
        InitializeComponent();
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        Title = $"Snapfield — Network  (v{v?.ToString(3)})";
        Closed += (_, _) => (DataContext as NetworkViewModel)?.ShutDown();
    }
}
