using System.Windows;
using Snapfield.App.ViewModels;

namespace Snapfield.App;

public partial class EngineWindow : Window
{
    public EngineWindow()
    {
        InitializeComponent();
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        Title = $"Snapfield — Input Engine  (v{v?.ToString(3)})";
        Closed += (_, _) => (DataContext as EngineViewModel)?.ShutDown();
    }
}
