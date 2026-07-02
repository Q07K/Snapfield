using System.Windows;
using Snapfield.App.ViewModels;

namespace Snapfield.App;

public partial class EngineWindow : Window
{
    public EngineWindow()
    {
        InitializeComponent();
        Closed += (_, _) => (DataContext as EngineViewModel)?.ShutDown();
    }
}
