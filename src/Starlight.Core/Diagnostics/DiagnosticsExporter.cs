using System.IO.Compression;
using System.Text;
using Serilog;

namespace Starlight.Diagnostics;

/// <summary>Builds the one zip Paul sends back after a bulk test: logs, crashes, reports, trace, settings, system info.</summary>
public sealed class DiagnosticsExporter(StarlightPaths paths, Func<string> settingsJson, Func<string> systemInfo)
{
    public string Export(string targetDir, int traceDays = 7)
    {
        Directory.CreateDirectory(targetDir);
        var zipPath = Path.Combine(targetDir, $"Starlight-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.zip");
        Log.Information("Exporting diagnostics to {Zip}", zipPath);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        AddFolder(zip, paths.LogsDir, "logs", null);
        AddFolder(zip, paths.CrashDir, "crashes", null);
        AddFolder(zip, paths.ReportsDir, "reports", null);
        AddFolder(zip, paths.TraceDir, "trace", DateTime.Now.AddDays(-traceDays));
        AddText(zip, "settings.json", Safe(settingsJson));
        AddText(zip, "system.txt", Safe(systemInfo));
        AddText(zip, "README.txt",
            $"Starlight {Logging.Version} diagnostics, made {DateTimeOffset.Now:O}.\r\n" +
            "logs/errors-*.log holds warnings and errors; logs/starlight-*.log everything.\r\n" +
            "crashes/ holds one file per crash; reports/ what you reported; trace/ each turn step by step.\r\n");
        return zipPath;
    }

    private static string Safe(Func<string> f)
    {
        try { return f(); } catch (Exception ex) { return "Could not collect: " + ex.Message; }
    }

    private static void AddFolder(ZipArchive zip, string dir, string name, DateTime? since)
    {
        if (!Directory.Exists(dir))
        {
            return;
        }
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            if (since is { } s && File.GetLastWriteTime(file) < s)
            {
                continue;
            }
            // Log files are open for writing (shared); read them with sharing allowed.
            try
            {
                using var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var entry = zip.CreateEntry($"{name}/{Path.GetFileName(file)}", CompressionLevel.Optimal);
                using var dst = entry.Open();
                src.CopyTo(dst);
            }
            catch (IOException ex)
            {
                AddText(zip, $"{name}/{Path.GetFileName(file)}.unreadable.txt", ex.Message);
            }
        }
    }

    private static void AddText(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        w.Write(text);
    }
}
