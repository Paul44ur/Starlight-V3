using Velopack;

namespace Starlight.App;

public static class Program
{
    /// <summary>Signalled by a second start of Starlight.exe, so the running one opens its window instead.</summary>
    internal const string ShowEventName = "Local\\Starlight.V3.Show";
    private const string MutexName = "Local\\Starlight.V3.Instance";

    [STAThread]
    public static int Main(string[] args)
    {
        // Velopack handles install, update and uninstall hooks and exits for those.
        VelopackApp.Build().Run();

        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var first);
        if (!first)
        {
            // One instance only: wake the running one.
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var show))
            {
                show.Set();
                show.Dispose();
            }
            return 0;
        }
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
