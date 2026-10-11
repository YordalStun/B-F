using System.Windows;
using BenchForge.App.Platform;
using BenchForge.Core.Analysis;

namespace BenchForge.App.Views;

public partial class FixWindow : Window
{
    private readonly CancellationTokenSource _cts = new();
    private readonly FixInfo _fix;

    public FixWindow(FixInfo fix)
    {
        InitializeComponent();
        _fix = fix;
        TitleText.Text = fix.Title;
        StatusText.Text = "Working…";
        Loaded += async (_, _) => await Run();
        Closing += (_, e) => { if (!CloseButton.IsEnabled) _cts.Cancel(); };
    }

    private void Append(string line) => Dispatcher.BeginInvoke(() => { Output.AppendText(line + Environment.NewLine); Output.ScrollToEnd(); });

    private async Task Run()
    {
        bool ok = await Task.Run(() => FixRunner.RunAsync(_fix.Id, Append, _cts.Token));
        StatusText.Text = ok ? (_fix.NeedsRestart ? "Done — restart the PC for it to take effect." : "Done.") : "Finished with problems — see the output above.";
        CloseButton.IsEnabled = true;
        CancelButton.IsEnabled = false;
        if (_fix.Id == FixId.ScanDisk && !ok) SpotFixButton.Visibility = Visibility.Visible;
    }

    private async void SpotFix_Click(object sender, RoutedEventArgs e)
    {
        SpotFixButton.IsEnabled = false;
        await FixRunner.ScheduleSpotFix(Append);
        Append("The repair will run the next time the PC restarts.");
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts.Cancel();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
