using System.ComponentModel;
using System.Diagnostics;
using Starlight.Tools;

namespace Starlight.Tools.Desktop;

/// <summary>The one tool for programs (plan 5.5): open, focus, close, list. Closing asks first.</summary>
public sealed class AppTools(AppCatalog catalog) : IToolProvider
{
    [Tool("app", AskForActions = ["close"], TimeoutSeconds = 20)]
    [Description("Opens, focuses, closes or lists programs and windows. open starts a program or opens a file or folder with its default program; focus brings an open window to the front; close closes a window; list shows the open windows.")]
    public async Task<string> App(
        [Description("open, focus, close or list")] string action,
        [Description("Program name (\"notepad\", \"Spotify\"), window title, or a file or folder path. Not needed for list.")] string? name = null,
        CancellationToken ct = default)
    {
        switch (action.Trim().ToLowerInvariant())
        {
            case "list":
                return List();
            case "open":
                return await OpenAsync(Required(name), ct);
            case "focus":
                return Focus(Required(name));
            case "close":
                return Close(Required(name));
            default:
                throw new ToolException("action must be open, focus, close or list.");
        }
    }

    private static string Required(string? name) =>
        string.IsNullOrWhiteSpace(name) ? throw new ToolException("Say which program or window.") : name.Trim();

    private static string List()
    {
        var windows = Win32.TopLevelWindows();
        if (windows.Count == 0)
        {
            return "No windows are open.";
        }
        return string.Join('\n', windows.Select(w => $"{w.Title}  ({w.Process}{(w.Minimized ? ", minimized" : "")})"));
    }

    private async Task<string> OpenAsync(string name, CancellationToken ct)
    {
        var expanded = Environment.ExpandEnvironmentVariables(name.Trim('"'));
        if (File.Exists(expanded) || Directory.Exists(expanded))
        {
            Start(new ProcessStartInfo(expanded) { UseShellExecute = true });
            return $"Opened {expanded}.";
        }
        var before = Win32.TopLevelWindows().Select(w => w.Handle).ToHashSet();
        var entry = AppMatcher.Builtin(name);
        if (entry is null)
        {
            var hits = AppMatcher.Find(name, catalog.Apps);
            if (hits.Count == 0)
            {
                throw new ToolException($"I can't find a program called '{name}'. Ask the user for its exact name.");
            }
            entry = hits[0]; // the closest match; Find sorts the best first
        }
        var psi = entry.Kind == AppKind.StoreApp
            ? new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{entry.Target}") { UseShellExecute = true }
            : new ProcessStartInfo(entry.Target) { UseShellExecute = true };
        Start(psi);
        // Act and check: wait for a new window to appear.
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(250, ct);
            var fresh = Win32.TopLevelWindows().FirstOrDefault(w => !before.Contains(w.Handle));
            if (fresh is not null)
            {
                return $"Opened {entry.Name}. Its window is \"{fresh.Title}\".";
            }
        }
        return $"Started {entry.Name}, but no new window appeared within 10 seconds. It may already be open or run in the background.";
    }

    private static void Start(ProcessStartInfo psi)
    {
        try
        {
            using var _ = Process.Start(psi);
        }
        catch (Win32Exception ex)
        {
            throw new ToolException($"Windows could not start it: {ex.Message}");
        }
    }

    private static WindowInfo FindWindow(string name)
    {
        var windows = Win32.TopLevelWindows();
        var wanted = AppMatcher.Normalize(name);
        var hit = windows.FirstOrDefault(w => AppMatcher.Normalize(w.Process) == wanted)
            ?? windows.FirstOrDefault(w => AppMatcher.Normalize(w.Title).Contains(wanted, StringComparison.Ordinal))
            ?? windows.FirstOrDefault(w => AppMatcher.Normalize(w.Process).Contains(wanted, StringComparison.Ordinal));
        return hit ?? throw new ToolException($"No open window matches '{name}'. Use action list to see the open windows.");
    }

    private static string Focus(string name)
    {
        var w = FindWindow(name);
        return Win32.Focus(w.Handle) || Win32.GetForegroundWindow() == w.Handle
            ? $"\"{w.Title}\" is in front now."
            : $"Windows did not let me bring \"{w.Title}\" to the front.";
    }

    private static string Close(string name)
    {
        var w = FindWindow(name);
        Win32.PostMessage(w.Handle, Win32.WM_CLOSE, 0, 0);
        return $"Asked \"{w.Title}\" to close. If it has unsaved work, it will ask the user.";
    }
}
