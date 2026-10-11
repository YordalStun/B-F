using System.Windows.Controls;
using System.Windows.Input;
using BenchForge.App.ViewModels;

namespace BenchForge.App.Views;

public partial class EventsView : UserControl
{
    public EventsView() { InitializeComponent(); }

    private void AskBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is EventsViewModel vm) vm.AskCommand.Execute(null);
    }
}
