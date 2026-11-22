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
        ["explorer"] = "explorer",
        ["command prompt"] = "cmd",
        ["terminal"] = "wt",
        ["task manager"] = "taskmgr",
        ["control panel"] = "control",
        ["paint"] = "mspaint",
        ["notepad"] = "notepad",
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
