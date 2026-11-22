using System.Text;
using Serilog;

namespace Starlight.Diagnostics;

/// <summary>Writes a crash file for every unhandled exception, before the process can end.</summary>
public static class CrashHandler
{
    private static StarlightPaths? s_paths;

    public static void Install(StarlightPaths paths)
    {
        s_paths = paths;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Record(e.ExceptionObject as Exception, "AppDomain", e.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Record(e.Exception, "UnobservedTask", terminating: false);
            e.SetObserved();
        };
    }

    /// <summary>Records an exception as a crash file plus an error log entry. Never throws.</summary>
    public static string? Record(Exception? ex, string source, bool terminating)
    {
        try
        {
            Log.Fatal(ex, "Unhandled exception from {Source}, terminating={Terminating}", source, terminating);
            if (s_paths is null)
            {
                return null;
            }
            var file = Path.Combine(s_paths.CrashDir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss-fff}.txt");
            var sb = new StringBuilder()
                .AppendLine($"Starlight {Logging.Version}")
                .AppendLine($"Time: {DateTimeOffset.Now:O}")
                .AppendLine($"Source: {source}, terminating: {terminating}")
                .AppendLine($"OS: {Environment.OSVersion}, 64-bit: {Environment.Is64BitProcess}, .NET {Environment.Version}")
                .AppendLine()
                .AppendLine(ex?.ToString() ?? "(no exception object)");
            File.WriteAllText(file, sb.ToString());
            if (terminating)
            {
                Log.CloseAndFlush();
            }
            return file;
        }
        catch
        {
            return null;
        }
    }
}
