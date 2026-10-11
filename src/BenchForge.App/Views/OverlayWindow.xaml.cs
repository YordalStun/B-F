using System.Windows;
using System.Windows.Input;
using BenchForge.App.ViewModels;

namespace BenchForge.App.Views;

/// <summary>Small always-on-top window with live temps/usage, visible over the 3D test. Drag it anywhere.</summary>
public partial class OverlayWindow : Window
{
    public OverlayWindow()
    {
        InitializeComponent();
        Left = SystemParameters.WorkArea.Right - 470;
        Top = SystemParameters.WorkArea.Top + 20;
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { } };
        // Re-assert top-most regularly: full-screen apps can push other windows down.
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) => { if (IsVisible) { Topmost = false; Topmost = true; } };
        timer.Start();
    }

    private void Close_Click(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is LiveRunViewModel vm) vm.ToggleOverlayCommand.Execute(null);
        else Hide();
    }
}
