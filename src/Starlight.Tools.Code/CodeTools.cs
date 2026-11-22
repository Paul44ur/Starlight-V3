using System.Collections.Concurrent;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Starlight.Tools.Code;

/// <summary>
/// code_write and code_run (plan 5.10). A script is always tried on a copy of the folder in the sandbox first;
/// only the exact code that passed may then run on the real folder, after the user's Yes and a backup.
/// </summary>
public sealed partial class CodeTools(ISandbox sandbox, string dataRoot) : IToolProvider
{
    public const long MaxFolderBytes = 1L << 30;
    public const int MaxFolderFiles = 5000;
    private const int MaxTries = 4;
    private static readonly TimeSpan RunTime = TimeSpan.FromSeconds(90);
    private readonly ConcurrentDictionary<string, int> _tries = new();

    private string ScriptsDir => Path.Combine(dataRoot, "scripts");
    private string WorkDir => Path.Combine(dataRoot, "sandbox");
    private string BackupsDir => Path.Combine(dataRoot, "backups");

    [Tool("code_write", TimeoutSeconds = 150, MaxResultChars = 8000)]
    [Description("Writes a Python script (standard library only, no network) and tests it in the sandbox on a copy of the folder. The script gets the folder as sys.argv[1]. Returns the output and what changed in the copy. If it fails, fix the code and call again. When the test looks right, use code_run.")]
    public async Task<string> Write(
        [Description("A short name for the script, for example rename_photos_by_date")] string name,
        [Description("The full Python code")] string code,
        [Description("The folder the script works on. A copy is used for the test. Optional.")] string? folder = null,
        CancellationToken ct = default)
    {
        var key = Name(name);
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ToolException("Give the Python code.");
        }
        var turn = ToolCallContext.Current?.TurnId ?? "";
        var tries = _tries.AddOrUpdate(turn + "/" + key, 1, (_, n) => n + 1);
        if (tries > MaxTries)
        {
            return $"Stop: {key} failed {MaxTries - 1} fixes in a row. Tell the user what goes wrong instead of trying again.";
        }
        Directory.CreateDirectory(ScriptsDir);
        var scriptFile = Path.Combine(ScriptsDir, key + ".py");
        await File.WriteAllTextAsync(scriptFile, code, ct);
        File.Delete(TestedFile(key));

        var copy = Path.Combine(WorkDir, key + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(copy);
        try
        {
            string? source = null;
            if (!string.IsNullOrWhiteSpace(folder))
            {
                source = RealFolder(folder);
                CopyFolder(source, copy, ct);
            }
            var before = Snapshot(copy);
            var result = await sandbox.RunAsync(scriptFile, copy, RunTime, ct);
            var changes = Changes(before, Snapshot(copy));
            var ok = result.ExitCode == 0 && !result.TimedOut;
            if (ok)
            {
                await File.WriteAllTextAsync(TestedFile(key), Hash(code) + "\n" + (source ?? ""), ct);
            }
            var sb = new StringBuilder();
            sb.Append(ok ? "Test passed" : result.TimedOut ? "Test stopped: it ran longer than 90 seconds" : $"Test failed (exit {result.ExitCode})");
            sb.Append(source is null ? " in an empty sandbox folder.\n" : $" on a copy of {source}.\n");
            sb.Append("Output:\n").Append(result.Output.Length == 0 ? "(none)" : result.Output.TrimEnd()).Append('\n');
            sb.Append("Changes in the copy:\n").Append(changes);
            sb.Append(ok
                ? source is null ? "\nThe script is saved. It can run with code_run." : $"\nIf this is what the user wants, run it on the real folder with code_run (name {key})."
                : $"\nFix the code and call code_write again ({MaxTries - tries} tries left).");
            return sb.ToString();
        }
        finally
        {
            TryDelete(copy);
        }
    }

    [Tool("code_run", Permission.Ask, TimeoutSeconds = 150)]
    [Description("Runs a script that passed its code_write test on the real folder, in the sandbox. Starlight keeps a backup of the folder first.")]
    public async Task<string> Run(
        [Description("The script's name from code_write")] string name,
        [Description("The real folder to run it on. Give the same folder it was tested on.")] string? folder = null,
        CancellationToken ct = default)
    {
        var key = Name(name);
        var scriptFile = Path.Combine(ScriptsDir, key + ".py");
        if (!File.Exists(scriptFile))
        {
            throw new ToolException($"There is no script named {key}. Write and test it with code_write first.");
        }
        var tested = File.Exists(TestedFile(key)) ? (await File.ReadAllTextAsync(TestedFile(key), ct)).Split('\n') : [];
        if (tested.Length < 2 || tested[0] != Hash(await File.ReadAllTextAsync(scriptFile, ct)))
        {
            throw new ToolException($"{key} has not passed a test in its current form. Test it with code_write first.");
        }
        var target = string.IsNullOrWhiteSpace(folder) ? null : RealFolder(folder);
        if (target is not null && tested[1].Length > 0 && !string.Equals(Path.GetFullPath(tested[1]), target, StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolException($"{key} was tested on {tested[1]}, not on {target}. Test it on this folder with code_write first.");
        }
        if (target is not null && tested[1].Length == 0)
        {
            throw new ToolException($"{key} was tested without a folder. Test it on {target} with code_write first.");
        }
        var runIn = target ?? Directory.CreateDirectory(Path.Combine(WorkDir, key + "-run")).FullName;
        string? backup = null;
        if (target is not null)
        {
            backup = Path.Combine(BackupsDir, $"{key}-{DateTime.Now:yyyyMMdd-HHmmss}");
            CopyFolder(target, backup, ct);
            PruneBackups();
        }
        var before = Snapshot(runIn);
        var result = await sandbox.RunAsync(scriptFile, runIn, RunTime, ct);
        var changes = Changes(before, Snapshot(runIn));
        var sb = new StringBuilder(result.ExitCode == 0 && !result.TimedOut ? "Done" : result.TimedOut ? "Stopped after 90 seconds" : $"Failed (exit {result.ExitCode})");
        sb.Append(target is null ? ".\n" : $" on {target}.\n");
        sb.Append("Output:\n").Append(result.Output.Length == 0 ? "(none)" : result.Output.TrimEnd()).Append('\n');
        sb.Append("Changes:\n").Append(changes);
        if (backup is not null)
        {
            sb.Append($"\nA backup of the folder as it was is in {backup}.");
        }
        return sb.ToString();
    }

    internal static string Name(string name)
    {
        var key = NameChars().Replace(name.Trim().ToLowerInvariant(), "_").Trim('_');
        if (key.Length == 0)
        {
            throw new ToolException("Give the script a name, for example rename_photos.");
        }
        return key.Length > 60 ? key[..60] : key;
    }

    [GeneratedRegex("[^a-z0-9_]+")]
    private static partial Regex NameChars();

    private string TestedFile(string key) => Path.Combine(ScriptsDir, key + ".tested");

    private static string Hash(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code.Replace("\r\n", "\n", StringComparison.Ordinal))));

    private string RealFolder(string folder)
    {
        var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(folder.Trim().Trim('"')));
        if (!Directory.Exists(full))
        {
            throw new ToolException($"The folder {full} does not exist.");
        }
        var root = Path.GetPathRoot(full);
        string[] off = [Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), dataRoot];
        if (string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), root?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            || off.Any(o => o.Length > 0 && full.StartsWith(o, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ToolException($"Scripts may not work on {full}. Pick a folder of the user's own files.");
        }
        return full;
    }

    internal static void CopyFolder(string from, string to, CancellationToken ct)
    {
        var files = Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories).Take(MaxFolderFiles + 1).ToList();
        if (files.Count > MaxFolderFiles)
        {
            throw new ToolException($"{from} has more than {MaxFolderFiles} files, too many to test on a copy. Pick a smaller folder.");
        }
        if (files.Sum(f => new FileInfo(f).Length) > MaxFolderBytes)
        {
            throw new ToolException($"{from} is larger than 1 GB, too large to test on a copy. Pick a smaller folder.");
        }
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        }
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            var dest = Path.Combine(to, Path.GetRelativePath(from, f));
            File.Copy(f, dest);
            File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(f));
        }
    }

    internal static Dictionary<string, (long Size, DateTime Time)> Snapshot(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Take(MaxFolderFiles * 2)
            .ToDictionary(f => Path.GetRelativePath(dir, f), f => { var i = new FileInfo(f); return (i.Length, i.LastWriteTimeUtc); }, StringComparer.OrdinalIgnoreCase);

    internal static string Changes(Dictionary<string, (long Size, DateTime Time)> before, Dictionary<string, (long Size, DateTime Time)> after)
    {
        var removed = before.Keys.Where(k => !after.ContainsKey(k)).Order().ToList();
        var added = after.Keys.Where(k => !before.ContainsKey(k)).Order().ToList();
        var changed = after.Keys.Where(k => before.TryGetValue(k, out var b) && b != after[k]).Order().ToList();
        if (removed.Count + added.Count + changed.Count == 0)
        {
            return "(no files changed)\n";
        }
        var lines = new List<string>();
        // A file that disappeared and a new one of the same size is shown as a rename.
        var pool = added.ToList();
        foreach (var r in removed)
        {
            var match = pool.FirstOrDefault(a => after[a].Size == before[r].Size);
            if (match is not null)
            {
                pool.Remove(match);
                lines.Add($"renamed {r} -> {match}");
            }
            else
            {
                lines.Add("removed " + r);
            }
        }
        lines.AddRange(pool.Select(a => "added " + a));
        lines.AddRange(changed.Select(c => "changed " + c));
        var shown = lines.Take(40).ToList();
        var text = string.Join('\n', shown);
        if (lines.Count > shown.Count)
        {
            text += $"\n… and {lines.Count - shown.Count} more";
        }
        return $"{removed.Count} removed, {added.Count} added, {changed.Count} changed\n{text}\n";
    }

    private void PruneBackups()
    {
        foreach (var old in Directory.GetDirectories(BackupsDir).OrderByDescending(Directory.GetCreationTimeUtc).Skip(5))
        {
            TryDelete(old);
        }
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
