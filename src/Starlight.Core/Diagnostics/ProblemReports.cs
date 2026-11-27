using System.Text.Json;
using Serilog;

namespace Starlight.Diagnostics;

/// <summary>"Report a problem": stores what the user said went wrong with the last minutes of trace.</summary>
public sealed class ProblemReports(StarlightPaths paths, SessionTrace trace)
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public string Save(string description)
    {
        description = SecretMasker.Mask(description);
        var file = Path.Combine(paths.ReportsDir, $"report-{DateTime.Now:yyyyMMdd-HHmmss-fff}.json");
        var report = new
        {
            time = DateTimeOffset.Now,
            version = Logging.Version,
            description,
            recentTrace = trace.Recent(TimeSpan.FromMinutes(5)),
        };
        File.WriteAllText(file, JsonSerializer.Serialize(report, Indented));
        Log.Warning("Problem reported by the user: {Description} ({File})", description, file);
        return file;
    }
}
