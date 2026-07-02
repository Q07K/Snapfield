using System.Windows;
using Snapfield.App.ViewModels;

namespace Snapfield.App;

public partial class NetworkWindow : Window
{
    public NetworkWindow()
    {
        InitializeComponent();
        Closed += (_, _) => (DataContext as NetworkViewModel)?.ShutDown();
    }
}
