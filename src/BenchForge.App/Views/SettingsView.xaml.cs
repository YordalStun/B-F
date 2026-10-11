using System.Windows;
using System.Windows.Controls;
using BenchForge.App.ViewModels;

namespace BenchForge.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() { InitializeComponent(); }

    private void SaveKey_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm) { vm.SetApiKey(KeyBox.Password); KeyBox.Clear(); }
    }

    private void Provider_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && DataContext is SettingsViewModel vm && rb.DataContext is ProviderOption p)
            rb.IsChecked = vm.Provider.Kind == p.Kind;
    }
}
