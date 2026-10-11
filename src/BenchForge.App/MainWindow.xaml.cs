using System.Windows;
using BenchForge.App.ViewModels;

namespace BenchForge.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        Loaded += (_, _) => _vm.Initialize(((App)Application.Current).StartupArgs);
        Closing += (s, e) =>
        {
            if (_vm.Live.IsRunning &&
                MessageBox.Show("A test is running. Stop it and close BenchForge?", "BenchForge", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                e.Cancel = true;
        };
    }
}
