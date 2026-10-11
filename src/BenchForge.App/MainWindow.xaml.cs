using System.Windows;
using BenchForge.App.ViewModels;

namespace BenchForge.App;

public partial class MainWindow : Window
{
    private void Logo_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => _vm.ToggleSidebar(ActualWidth);

    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        // Fit the screen: laptops with small or high-DPI screens open maximised, big screens get a comfortable window.
        var wa = SystemParameters.WorkArea;
        Width = Math.Min(1440, wa.Width * 0.92);
        Height = Math.Min(900, wa.Height * 0.92);
        if (wa.Width < 1400 || wa.Height < 850) WindowState = WindowState.Maximized;
        SizeChanged += (_, _) => _vm.UpdateLayoutMode(ActualWidth);
        Loaded += (_, _) => _vm.Initialize(((App)Application.Current).StartupArgs);
        Closing += (s, e) =>
        {
            if (_vm.Live.IsRunning &&
                MessageBox.Show("A test is running. Stop it and close BenchForge?", "BenchForge", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                e.Cancel = true;
        };
    }
}
