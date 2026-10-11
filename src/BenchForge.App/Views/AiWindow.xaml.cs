using System.Windows;
using BenchForge.App.ViewModels;

namespace BenchForge.App.Views;

public partial class AiWindow : Window
{
    public AiWindow(AiChatViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
