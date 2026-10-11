using System.Threading;
using System.Windows;
using System.Windows.Threading;
using BenchForge.Core.Persistence;

namespace BenchForge.App;

public partial class App : Application
{
    private Mutex? _single;
    public string[] StartupArgs { get; set; } = Array.Empty<string>();

    protected override void OnStartup(StartupEventArgs e)
    {
        _single = new Mutex(true, "BenchForge.SingleInstance", out bool first);
        if (!first)
        {
            MessageBox.Show("BenchForge is already running.", "BenchForge", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => DataPaths.AppLogWrite("Unhandled: " + ex.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, ex) => { DataPaths.AppLogWrite("Unobserved: " + ex.Exception); ex.SetObserved(); };
        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        DataPaths.AppLogWrite("UI error: " + e.Exception);
        MessageBox.Show("Something went wrong:\n\n" + e.Exception.Message + "\n\nDetails were saved to " + DataPaths.AppLog, "BenchForge", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { Services.Instance.Dispose(); } catch { }
        _single?.Dispose();
        base.OnExit(e);
    }
}
