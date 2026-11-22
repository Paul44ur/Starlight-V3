namespace Starlight;

/// <summary>Where Starlight keeps its files. Everything hangs off one data folder so tests can redirect it.</summary>
public sealed class StarlightPaths
{
    public StarlightPaths(string dataRoot, string appRoot)
    {
        DataRoot = dataRoot;
        AppRoot = appRoot;
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(TraceDir);
        Directory.CreateDirectory(CrashDir);
        Directory.CreateDirectory(ReportsDir);
    }

    /// <summary>%AppData%\Starlight by default: settings, database, logs.</summary>
    public string DataRoot { get; }

    /// <summary>The folder Starlight.exe runs from: bundled runtimes and models' defaults.</summary>
    public string AppRoot { get; }

    public string SettingsFile => Path.Combine(DataRoot, "settings.json");
    public string DatabaseFile => Path.Combine(DataRoot, "starlight.db");
    public string LogsDir => Path.Combine(DataRoot, "logs");
    public string TraceDir => Path.Combine(DataRoot, "trace");
    public string CrashDir => Path.Combine(DataRoot, "crashes");
    public string ReportsDir => Path.Combine(DataRoot, "reports");
    public string RuntimeDir => Path.Combine(AppRoot, "runtime");

    public static StarlightPaths Default()
    {
        var data = Environment.GetEnvironmentVariable("STARLIGHT_DATA");
        if (string.IsNullOrWhiteSpace(data))
        {
            data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Starlight");
        }
        return new StarlightPaths(data, AppContext.BaseDirectory);
    }
}
