using System.Windows;
using System.Windows.Threading;
using Starlight.Diagnostics;
using Wpf.Ui.Appearance;

namespace Starlight.App;

public partial class App : Application
{
    private Shell? _shell;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ApplicationThemeManager.ApplySystemTheme();
        DispatcherUnhandledException += OnDispatcherException;
        try
        {
            _shell = new Shell(this);
            await _shell.StartAsync();
        }
        catch (Exception ex)
        {
            // Without this the process would stay alive with no tray icon and no window.
            CrashHandler.Record(ex, "startup", terminating: true);
            MessageBox.Show("Starlight could not start: " + ex.Message + "\n\nThe details are in the error log.", "Starlight", MessageBoxButton.OK, MessageBoxImage.Error);
            _shell?.Dispose();
            _shell = null;
            Shutdown(1);
        }
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Logged as a crash, but the app keeps running: a broken window must not take the assistant down.
        CrashHandler.Record(e.Exception, "WPF dispatcher", terminating: false);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shell?.Dispose();
        base.OnExit(e);
    }
}
