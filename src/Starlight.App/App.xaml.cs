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
        _shell = new Shell(this);
        await _shell.StartAsync();
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
