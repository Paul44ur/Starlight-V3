namespace Starlight.Tools.Desktop;

/// <summary>A program the user can start: a Start menu shortcut, a Store app or a known built-in.</summary>
public sealed record AppEntry(string Name, string Target, AppKind Kind);

public enum AppKind { Shortcut, StoreApp, Executable }

/// <summary>Picks the app a spoken name means. Plain logic, so it is tested on Linux too.</summary>
public static class AppMatcher
{
    /// <summary>Spoken names for programs whose Start menu name differs.</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["calculator"] = "calc",
        ["file explorer"] = "explorer",
        ["files"] = "explorer",
        ["command prompt"] = "cmd",
        ["terminal"] = "wt",
        ["task manager"] = "taskmgr",
        ["control panel"] = "control",
        ["paint"] = "mspaint",
        ["settings"] = "ms-settings:",
    };

    public static AppEntry? Builtin(string name) =>
        Aliases.TryGetValue(Normalize(name), out var exe) ? new AppEntry(name, exe, AppKind.Executable) : null;

    /// <summary>Best match by name: exact, then starts with, then all words contained. Null if none or unclear.</summary>
    public static IReadOnlyList<AppEntry> Find(string name, IEnumerable<AppEntry> apps)
    {
        var wanted = Normalize(name);
        if (wanted.Length == 0)
        {
            return [];
        }
        var list = apps.ToList();
        var exact = list.Where(a => Normalize(a.Name) == wanted).ToList();
        if (exact.Count > 0)
        {
            return Prefer(exact);
        }
        var starts = list.Where(a => Normalize(a.Name).StartsWith(wanted, StringComparison.Ordinal)).ToList();
        if (starts.Count > 0)
        {
            return Prefer(starts);
        }
        var words = wanted.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return Prefer(list.Where(a =>
        {
            var n = Normalize(a.Name);
            return words.All(w => n.Contains(w, StringComparison.Ordinal));
        }).ToList());
    }

    /// <summary>
    /// Picks one app for a spoken name. A loose "all words contained" match with several different
    /// apps is not guessed: the candidates come back so the model can ask the user.
    /// </summary>
    public static (AppEntry? Pick, IReadOnlyList<AppEntry> Candidates) Choose(string name, IEnumerable<AppEntry> apps)
    {
        var wanted = Normalize(name);
        var list = apps.ToList();
        var hits = Find(name, list);
        if (hits.Count <= 1)
        {
            return (hits.Count == 1 ? hits[0] : null, hits);
        }
        var close = list.Any(a => Normalize(a.Name) == wanted || Normalize(a.Name).StartsWith(wanted, StringComparison.Ordinal));
        return close ? (hits[0], hits) : (null, hits.Take(5).ToList());
    }

    /// <summary>
    /// The window a name means: the process name first, then a title or process that contains the
    /// name. When <paramref name="unique"/> is set (closing), several different matches are refused.
    /// </summary>
    public static int PickWindow(string name, IReadOnlyList<(string Title, string Process)> windows, bool unique, out string? problem)
    {
        problem = null;
        var wanted = Normalize(name);
        if (wanted.Length == 0)
        {
            problem = "Say the window's name in words.";
            return -1;
        }
        var byProcess = Indexes(windows, w => Normalize(w.Process) == wanted);
        var byTitle = Indexes(windows, w => Normalize(w.Title) == wanted);
        var loose = Indexes(windows, w => Normalize(w.Title).Contains(wanted, StringComparison.Ordinal) || Normalize(w.Process).Contains(wanted, StringComparison.Ordinal));
        var tier = byProcess.Count > 0 ? byProcess : byTitle.Count > 0 ? byTitle : loose;
        if (tier.Count == 0)
        {
            problem = $"No open window matches '{name}'. Use action list to see the open windows.";
            return -1;
        }
        if (unique && tier.Count > 1)
        {
            problem = $"Several windows match '{name}': " + string.Join("; ", tier.Take(5).Select(i => $"\"{windows[i].Title}\" ({windows[i].Process})")) + ". Ask the user which one, then use its full title.";
            return -1;
        }
        return tier[0];
    }

    private static List<int> Indexes(IReadOnlyList<(string Title, string Process)> windows, Func<(string Title, string Process), bool> match) =>
        Enumerable.Range(0, windows.Count).Where(i => match(windows[i])).ToList();

    /// <summary>File types that run code when opened. Opening one of these asks the user first.</summary>
    private static readonly HashSet<string> RunnableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".ps1", ".psm1", ".psd1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh",
        ".msi", ".msp", ".mst", ".hta", ".scr", ".lnk", ".url", ".pif", ".cpl", ".jar", ".reg", ".appref-ms",
        ".application", ".msc", ".inf", ".scf", ".xll", ".docm", ".dotm", ".xlsm", ".xltm", ".xlam", ".pptm",
        ".potm", ".ppam", ".iso", ".img", ".vhd", ".vhdx", ".appx", ".appxbundle", ".msix", ".msixbundle",
        ".appinstaller", ".library-ms", ".search-ms", ".settingcontent-ms", ".chm", ".py", ".pyw", ".sys",
        ".dll", ".ocx", ".gadget", ".diagcab", ".website", ".shs", ".ws",
    };

    /// <summary>True when opening this file could run code.</summary>
    public static bool IsRunnable(string path)
    {
        var ext = Path.GetExtension(path.Trim().Trim('"').TrimEnd('.', ' '));
        return ext.Length == 0 || RunnableExtensions.Contains(ext);
    }

    /// <summary>Shorter names first ("Word" before "Word Viewer"), duplicates by name removed.</summary>
    private static List<AppEntry> Prefer(List<AppEntry> hits) =>
        hits.GroupBy(a => Normalize(a.Name)).Select(g => g.OrderBy(a => a.Kind).First())
            .OrderBy(a => a.Name.Length).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public static string Normalize(string s)
    {
        var chars = s.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray();
        var text = string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        foreach (var filler in new[] { "the ", "app ", "program " })
        {
            if (text.StartsWith(filler, StringComparison.Ordinal))
            {
                text = text[filler.Length..];
            }
        }
        foreach (var filler in new[] { " app", " program", " application" })
        {
            if (text.EndsWith(filler, StringComparison.Ordinal))
            {
                text = text[..^filler.Length];
            }
        }
        return text;
    }
}
