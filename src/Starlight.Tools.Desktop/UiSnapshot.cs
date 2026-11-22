using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using Serilog;
using Starlight.Events;

namespace Starlight.Tools.Desktop;

/// <summary>A control in the front window, numbered so a small model can say "click 7" (idea from Windows-MCP).</summary>
public sealed record UiElement(int Number, string Name, string Kind, ScreenRect Box, bool IsPassword)
{
    public override string ToString() => $"[{Number}] {Kind} \"{Name}\"";
}

/// <summary>Reads the clickable controls of the front window through UI Automation, and keeps the last list.</summary>
public sealed class UiSnapshot : IDisposable
{
    public const int MaxElements = 80;

    private static readonly ControlType[] Interactive =
    [
        ControlType.Button, ControlType.MenuItem, ControlType.Hyperlink, ControlType.Edit, ControlType.CheckBox, ControlType.RadioButton,
        ControlType.ComboBox, ControlType.ListItem, ControlType.TabItem, ControlType.TreeItem, ControlType.SplitButton, ControlType.Document,
        ControlType.Slider, ControlType.Spinner, ControlType.DataItem, ControlType.MenuBar,
    ];

    private readonly UIA3Automation _automation = new();
    private readonly Lock _gate = new();
    private List<UiElement> _last = [];

    public IReadOnlyList<UiElement> Last
    {
        get { lock (_gate) { return _last; } }
    }

    /// <summary>The front window's title and its controls, numbered from 1.</summary>
    public (string Title, List<UiElement> Elements) Take(nint hwnd)
    {
        var list = new List<UiElement>();
        string title = "";
        try
        {
            var window = _automation.FromHandle(hwnd);
            title = window.Name ?? "";
            var windowBox = ScreenCapture.WindowRect(hwnd);
            var cf = _automation.ConditionFactory;
            ConditionBase condition = cf.ByControlType(Interactive[0]);
            foreach (var t in Interactive.Skip(1))
            {
                condition = condition.Or(cf.ByControlType(t));
            }
            foreach (var e in window.FindAll(TreeScope.Descendants, condition))
            {
                if (list.Count >= MaxElements)
                {
                    break;
                }
                var item = Describe(e, list.Count + 1, windowBox);
                if (item is not null)
                {
                    list.Add(item);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "UI Automation could not read window {Handle}", hwnd);
        }
        lock (_gate)
        {
            _last = list;
        }
        return (title, list);
    }

    private static UiElement? Describe(AutomationElement e, int number, ScreenRect window)
    {
        try
        {
            if (e.IsOffscreen || !e.IsEnabled)
            {
                return null;
            }
            var r = e.BoundingRectangle;
            if (r.Width < 4 || r.Height < 4)
            {
                return null;
            }
            var box = new ScreenRect(r.X, r.Y, r.Width, r.Height);
            if (box.CenterX < window.X || box.CenterY < window.Y || box.CenterX > window.X + window.Width || box.CenterY > window.Y + window.Height)
            {
                return null;
            }
            var name = e.Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = e.Properties.HelpText.ValueOrDefault ?? e.AutomationId ?? "";
            }
            if (string.IsNullOrWhiteSpace(name) && e.ControlType is not (ControlType.Edit or ControlType.Document))
            {
                return null;
            }
            var isPassword = e.Properties.IsPassword.ValueOrDefault;
            return new UiElement(number, name.Length > 80 ? name[..80] : name, e.ControlType.ToString(), box, isPassword);
        }
        catch (Exception)
        {
            return null; // elements vanish while being read
        }
    }

    /// <summary>The focused control, to check for password fields before typing.</summary>
    public bool FocusIsPassword()
    {
        try
        {
            return _automation.FocusedElement()?.Properties.IsPassword.ValueOrDefault ?? false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Dispose() => _automation.Dispose();
}
