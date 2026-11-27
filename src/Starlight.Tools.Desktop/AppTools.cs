using System.ComponentModel;
using System.Diagnostics;
using Starlight.Tools;

namespace Starlight.Tools.Desktop;

/// <summary>The one tool for programs (plan 5.5): open, focus, close, list. Closing asks first,
/// and so does opening a file that can run code.</summary>
public sealed class AppTools(AppCatalog catalog) : IToolProvider, IPermissionHint
{
    /// <summary>Opening a path to a script, installer or program asks the user (the model may have
    /// been told to by a web page). Programs from the Start menu still open without a question.</summary>
    public Permission? PermissionFor(string tool, IDictionary<string, object?> arguments)
    {
        if (tool != "app" || !arguments.TryGetValue("action", out var a) || !ToolDispatcher.ArgumentText(a).Trim().Equals("open", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var name = arguments.TryGetValue("name", out var n) ? ToolDispatcher.ArgumentText(n) : "";
        var path = Environment.ExpandEnvironmentVariables(name.Trim().Trim('"'));
        return path.Length > 0 && File.Exists(path) && AppMatcher.IsRunnable(path) ? Permission.Ask : null;
    }

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
            var (pick, candidates) = AppMatcher.Choose(name, catalog.Apps);
            if (candidates.Count == 0)
            {
                throw new ToolException($"I can't find a program called '{name}'. Ask the user for its exact name.");
            }
            entry = pick ?? throw new ToolException($"Several programs could be '{name}': {string.Join(", ", candidates.Select(c => c.Name))}. Ask the user which one.");
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

    private static WindowInfo FindWindow(string name, bool unique)
    {
        var windows = Win32.TopLevelWindows();
        var i = AppMatcher.PickWindow(name, windows.Select(w => (w.Title, w.Process)).ToList(), unique, out var problem);
        return i >= 0 ? windows[i] : throw new ToolException(problem!);
    }

    private static string Focus(string name)
    {
        var w = FindWindow(name, unique: false);
        return Win32.Focus(w.Handle) || Win32.GetForegroundWindow() == w.Handle
            ? $"\"{w.Title}\" is in front now."
            : $"Windows did not let me bring \"{w.Title}\" to the front.";
    }

    private static string Close(string name)
    {
        var w = FindWindow(name, unique: true);
        Win32.PostMessage(w.Handle, Win32.WM_CLOSE, 0, 0);
        return $"Asked \"{w.Title}\" to close. If it has unsaved work, it will ask the user.";
    }
}
