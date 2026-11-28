using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.AI;
using Starlight.Events;
using Starlight.Tools;

namespace Starlight.Tools.Desktop;

/// <summary>
/// Seeing the screen and using mouse and keyboard (plan 5.5). Targets are found by UI Automation first,
/// then by OCR. Every action is checked afterwards: if nothing changed, the model is told so.
/// </summary>
public sealed class DesktopTools(UiSnapshot ui, EventHub events, Func<bool> hasVision) : IToolProvider
{
    private sealed record Found(ScreenRect Box, string Label, bool Exact, string How);

    /// <summary>The last picture the model saw, so its x,y can be mapped to the screen.</summary>
    private PictureFrame? _lastPicture;

    [Tool("screen_look", TimeoutSeconds = 30)]
    [Description("Looks at the front window: its title and a numbered list of its buttons, fields and links, plus a picture or the text on screen. Use it before clicking or typing, and to answer questions about what is on screen.")]
    public async Task<ToolOutput> Look(
        [Description("window (default: the front window) or screen (all monitors)")] string? area = null,
        CancellationToken ct = default)
    {
        var hwnd = Win32.GetForegroundWindow();
        var (title, elements) = ui.Take(hwnd);
        var region = area?.Trim().ToLowerInvariant() == "screen" ? ScreenCapture.VirtualDesktop() : ScreenCapture.WindowRect(hwnd);
        using var shot = ScreenCapture.Capture(region);
        var text = new StringBuilder();
        text.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Front window: \"{title}\"");
        text.AppendLine(elements.Count == 0 ? "No controls could be read from it." : "Controls (use the number to click or point):");
        foreach (var e in elements)
        {
            text.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"{e}{(e.IsPassword ? " (password field)" : "")}");
        }
        if (hasVision())
        {
            var frame = shot.Frame();
            _lastPicture = frame;
            text.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Picture: {(int)(shot.Area.Width * frame.Scale)}x{(int)(shot.Area.Height * frame.Scale)} pixels. Prefer control numbers; x,y means a point in this picture.");
            return new ToolOutput(text.ToString().TrimEnd(), [new DataContent(shot.ToPng(), "image/png")]);
        }
        _lastPicture = null;
        var lines = await Ocr.ReadAsync(shot, ct);
        text.AppendLine("Text on screen:");
        foreach (var l in lines.Take(80))
        {
            text.AppendLine(l.Text);
        }
        return new ToolOutput(text.ToString().TrimEnd(), []);
    }

    [Tool("screen_find", TimeoutSeconds = 20)]
    [Description("Finds a control or text on screen by name, for example \"Save\" or \"Settings\". Returns where it is and how it was found.")]
    public async Task<string> Find([Description("What to find: its name or visible text")] string target, CancellationToken ct = default)
    {
        var f = await ResolveAsync(target, ct);
        // No x,y here: they would be screen pixels, while click and point read x,y in the last picture.
        return $"Found {f.Label} ({f.How}). To click or point at it, pass the same target: \"{target}\".";
    }

    [Tool("screen_point", TimeoutSeconds = 20)]
    [Description("Shows the user where something is: draws a line from the orb to it and a frame around it. Use it to answer \"where is…\" questions.")]
    public async Task<string> Point(
        [Description("A control number from screen_look, a name like \"Save\", or x,y in the last screen_look picture")] string target,
        [Description("Short label to show next to it")] string? label = null,
        CancellationToken ct = default)
    {
        var f = await ResolveAsync(target, ct);
        events.Publish(new PointAt(f.Box, label ?? f.Label, f.Exact, TimeSpan.FromSeconds(8)));
        return $"Pointing at {f.Label} ({f.How}).";
    }

    [Tool("input_click", Permission.Ask, TimeoutSeconds = 20)]
    [Description("Clicks a control. Look first with screen_look and give the control's number, or its name, or x,y.")]
    public async Task<string> Click(
        [Description("A control number from screen_look, a name, or x,y in the last screen_look picture")] string target,
        [Description("left (default), right or middle")] string? button = null,
        [Description("true for a double-click")] bool doubleClick = false,
        CancellationToken ct = default)
    {
        GuardUser();
        var f = await ResolveAsync(target, ct);
        return await ActAsync($"Clicked {f.Label}", () => Input.Click(f.Box.CenterX, f.Box.CenterY, (button ?? "left").ToLowerInvariant(), doubleClick), ct);
    }

    [Tool("input_type", Permission.Ask, TimeoutSeconds = 60)]
    [Description("Types text into the focused field, or into a field you name first. Never use it for passwords.")]
    public async Task<string> Type(
        [Description("The text to type")] string text,
        [Description("Optional field to click first: a control number, a name, or x,y in the last screen_look picture")] string? target = null,
        [Description("true to press Enter afterwards")] bool pressEnter = false,
        CancellationToken ct = default)
    {
        GuardUser();
        if (!string.IsNullOrWhiteSpace(target))
        {
            var f = await ResolveAsync(target, ct);
            if (ui.Last.FirstOrDefault(e => e.Box == f.Box)?.IsPassword == true)
            {
                throw new ToolException("That is a password field. Starlight never types passwords; the user types it.");
            }
            Input.Click(f.Box.CenterX, f.Box.CenterY);
            await Task.Delay(150, ct);
        }
        if (ui.FocusIsPassword())
        {
            throw new ToolException("The focused field is a password field. Starlight never types passwords; the user types it.");
        }
        return await ActAsync($"Typed {text.Length} characters", () =>
        {
            Input.Type(text, ct);
            if (pressEnter)
            {
                Input.Tap(0x0D);
            }
        }, ct);
    }

    [Tool("input_keys", Permission.Ask, TimeoutSeconds = 10)]
    [Description("Presses a key or shortcut in the front window, like ctrl+s, alt+tab, enter, esc or win+d.")]
    public Task<string> Keys([Description("Keys joined by +, for example ctrl+shift+t")] string keys, CancellationToken ct = default)
    {
        GuardUser();
        return ActAsync($"Pressed {keys}", () => Input.Keys(keys), ct);
    }

    [Tool("window", TimeoutSeconds = 10)]
    [Description("Arranges windows: minimize, maximize, restore, snap_left, snap_right, or minimize_all (show the desktop).")]
    public static string Window(
        [Description("minimize, maximize, restore, snap_left, snap_right or minimize_all")] string action,
        [Description("Window title or program name; the front window if empty")] string? name = null)
    {
        var a = action.Trim().ToLowerInvariant();
        if (a == "minimize_all")
        {
            Input.Keys("win+d");
            return "Showed the desktop.";
        }
        if (a is not ("minimize" or "maximize" or "restore" or "snap_left" or "snap_right"))
        {
            throw new ToolException("action must be minimize, maximize, restore, snap_left, snap_right or minimize_all.");
        }
        var windows = Win32.TopLevelWindows();
        WindowInfo w;
        if (string.IsNullOrWhiteSpace(name))
        {
            w = windows.FirstOrDefault(x => x.Handle == Win32.GetForegroundWindow())
                ?? throw new ToolException("The front window is Starlight's own or has no title. Give the window's name.");
        }
        else
        {
            // The same matching as the app tool: several matching windows means asking, not guessing.
            var i = AppMatcher.PickWindow(name, windows.Select(x => (x.Title, x.Process)).ToList(), unique: true, out var problem);
            w = i >= 0 ? windows[i] : throw new ToolException(problem!);
        }
        // ShowWindow's result says whether the window was visible before, not whether it worked; only focus can fail.
        var ok = a switch
        {
            "minimize" => Show(w.Handle, ShowMinimized),
            "maximize" => Show(w.Handle, ShowMaximized),
            "restore" => Show(w.Handle, Win32.SW_RESTORE),
            _ => Snap(w.Handle, a == "snap_left"),
        };
        if (!ok)
        {
            throw new ToolException($"Windows did not let me change \"{w.Title}\". It may run as administrator.");
        }
        return $"{a.Replace('_', ' ')}: \"{w.Title}\".";
    }

    private const int ShowMinimized = 6;
    private const int ShowMaximized = 3;

    private static bool Show(nint handle, int command)
    {
        Win32.ShowWindow(handle, command);
        return true;
    }

    private static bool Snap(nint handle, bool left)
    {
        if (!Win32.Focus(handle))
        {
            return false;
        }
        GuardUser();
        Thread.Sleep(100);
        Input.Keys(left ? "win+left" : "win+right");
        return true;
    }

    /// <summary>Input waits when the user is typing or moving the mouse (plan 5.5).</summary>
    private static void GuardUser()
    {
        if (Input.UserIsActive(TimeSpan.FromMilliseconds(500)))
        {
            throw new ToolException("The user is using the mouse or keyboard right now. Wait and ask them before trying again.");
        }
    }

    /// <summary>Runs an action with the red frame on, then checks that the screen changed.</summary>
    private async Task<string> ActAsync(string what, Action act, CancellationToken ct)
    {
        var hwnd = Win32.GetForegroundWindow();
        var area = ScreenCapture.WindowRect(hwnd);
        var before = ScreenCapture.Fingerprint(area);
        events.Publish(new ControlActive(true));
        try
        {
            ct.ThrowIfCancellationRequested();
            act();
        }
        finally
        {
            events.Publish(new ControlActive(false));
        }
        await Task.Delay(450, ct);
        var nowFront = Win32.GetForegroundWindow();
        if (nowFront != hwnd)
        {
            var title = Win32.TopLevelWindows().FirstOrDefault(w => w.Handle == nowFront)?.Title;
            return $"{what}. A different window is in front now: \"{title}\".";
        }
        var after = ScreenCapture.Fingerprint(area);
        return after != before
            ? $"{what}. The window changed."
            : $"{what}. Nothing visibly changed; it may not have worked. Look again before the next step.";
    }

    private async Task<Found> ResolveAsync(string target, CancellationToken ct)
    {
        var spec = TargetParser.Parse(target);
        if (spec.Point is { } p)
        {
            // x,y come from the last picture (shrunk, starting at the window's corner); without one
            // they are screen pixels.
            var (sx, sy) = _lastPicture is { } frame ? TargetParser.PictureToScreen(p.X, p.Y, frame) : p;
            return new Found(new ScreenRect(sx - 10, sy - 10, 20, 20), $"{p.X},{p.Y}", false, _lastPicture is null ? "screen coordinates" : "picture coordinates");
        }
        if (spec.Element is { } n)
        {
            var e = ui.Last.FirstOrDefault(x => x.Number == n)
                ?? throw new ToolException($"There is no control {n}. Use screen_look first; the numbers change when the window changes.");
            return new Found(e.Box, e.ToString(), true, "UI Automation");
        }
        var name = spec.Name!;
        var (_, elements) = ui.Take(Win32.GetForegroundWindow());
        var best = elements.Select(e => (e, score: TargetParser.Match(name, e.Name))).Where(x => x.score > 0).OrderByDescending(x => x.score).FirstOrDefault();
        if (best.e is not null)
        {
            return new Found(best.e.Box, best.e.ToString(), true, "UI Automation");
        }
        using var shot = ScreenCapture.Capture(ScreenCapture.VirtualDesktop());
        var lines = await Ocr.ReadAsync(shot, ct);
        var text = lines.Select(l => (l, score: TargetParser.Match(name, l.Text))).Where(x => x.score > 0).OrderByDescending(x => x.score).FirstOrDefault();
        if (text.l is not null)
        {
            return new Found(text.l.Box, $"text \"{text.l.Text}\"", false, "OCR, a guess");
        }
        throw new ToolException($"I can't find \"{name}\" on screen. Use screen_look to see what is there.");
    }
}
