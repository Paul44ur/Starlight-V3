using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Starlight.Tools;

namespace Starlight.Tools.Pc;

/// <summary>Facts about this PC and finding files. Read-only, so both are Safe.</summary>
public sealed class PcTools : IToolProvider
{
    public const int MaxFindResults = 25;
    private readonly IReadOnlyList<string> _defaultRoots;

    public PcTools(IReadOnlyList<string>? defaultRoots = null)
    {
        _defaultRoots = defaultRoots ?? DefaultRoots();
    }

    [Tool("system_info"), Description("Facts about this PC: Windows version, CPU, memory, free disk space, uptime. Use it for questions about the computer itself.")]
    public string SystemInfo()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        sb.AppendLine($"Computer: {Environment.MachineName}, user {Environment.UserName}");
        sb.AppendLine($"CPU: {Environment.ProcessorCount} logical cores");
        var mem = GC.GetGCMemoryInfo();
        sb.AppendLine($"Memory: {Gb(mem.TotalAvailableMemoryBytes)} total");
        sb.AppendLine($"Uptime: {FormatSpan(TimeSpan.FromMilliseconds(Environment.TickCount64))}");
        foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable))
        {
            sb.AppendLine($"Drive {d.Name}: {Gb(d.AvailableFreeSpace)} free of {Gb(d.TotalSize)}");
        }
        return sb.ToString().TrimEnd();
    }

    [Tool("files_find", TimeoutSeconds = 20), Description("Finds files or folders by name. Searches the user's Desktop, Documents, Downloads, Pictures, Music and Videos unless a folder is given. Returns full paths, newest first.")]
    public string FindFiles(
        [Description("Part of the name, or a pattern like *.pdf or report*.docx")] string name,
        [Description("Optional folder to search in, for example D:\\Projects")] string? folder = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ToolException("Give part of the file name to search for.");
        }
        IReadOnlyList<string> roots = _defaultRoots;
        if (!string.IsNullOrWhiteSpace(folder))
        {
            var full = Environment.ExpandEnvironmentVariables(folder);
            if (!Directory.Exists(full))
            {
                throw new ToolException($"The folder {folder} does not exist.");
            }
            roots = [full];
        }
        var pattern = name.Contains('*') || name.Contains('?') ? name : $"*{name}*";
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
            AttributesToSkip = FileAttributes.System,
        };
        var hits = new List<FileSystemInfo>();
        var scanned = 0;
        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(root, pattern, options))
            {
                ct.ThrowIfCancellationRequested();
                hits.Add(Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path));
                if (++scanned >= 2000)
                {
                    break;
                }
            }
        }
        if (hits.Count == 0)
        {
            return $"No files matching '{name}' in {string.Join(", ", roots)}.";
        }
        var lines = hits.OrderByDescending(h => h.LastWriteTime).Take(MaxFindResults)
            .Select(h => h is FileInfo f ? $"{f.FullName}  ({Size(f.Length)}, {f.LastWriteTime:yyyy-MM-dd})" : $"{h.FullName}\\  (folder)");
        var more = hits.Count > MaxFindResults ? $"\n…and {hits.Count - MaxFindResults} more. Ask for a narrower name or folder." : "";
        return string.Join('\n', lines) + more;
    }

    internal static IReadOnlyList<string> DefaultRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(home, "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        }.Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string Gb(long bytes) => $"{bytes / 1024d / 1024 / 1024:0.0} GB";

    private static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024d:0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024:0.0} MB",
        _ => Gb(bytes),
    };

    private static string FormatSpan(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays} d {t.Hours} h" : $"{t.Hours} h {t.Minutes} min";
}
