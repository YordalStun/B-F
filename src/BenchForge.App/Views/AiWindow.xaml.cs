using System.Windows;
using BenchForge.App.ViewModels;

namespace BenchForge.App.Views;

public partial class AiWindow : Window
{
    public AiWindow(AiChatViewModel vm)
    {
        InitializeComponent();
        var wa = SystemParameters.WorkArea;
        Width = Math.Min(Width, wa.Width * 0.9);
        Height = Math.Min(Height, wa.Height * 0.9);
        DataContext = vm;
    }
}
