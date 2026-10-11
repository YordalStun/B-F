using System.Windows.Controls;
using BenchForge.App.ViewModels;

namespace BenchForge.App.Views;

public partial class CrashesView : UserControl
{
    public CrashesView() { InitializeComponent(); }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is CrashesViewModel vm) vm.Selection = List.SelectedItems.Cast<CrashItem>().ToList();
    }
}
