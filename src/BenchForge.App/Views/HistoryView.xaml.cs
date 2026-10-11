using System.Windows.Controls;
using System.Windows.Input;
using BenchForge.App.ViewModels;
using BenchForge.Core.Model;

namespace BenchForge.App.Views;

public partial class HistoryView : UserControl
{
    public HistoryView() { InitializeComponent(); }

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is HistoryViewModel vm) vm.Selection = Grid.SelectedItems.Cast<RunSummary>().ToList();
    }

    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is HistoryViewModel vm && Grid.SelectedItem != null) vm.OpenCommand.Execute(null);
    }
}
