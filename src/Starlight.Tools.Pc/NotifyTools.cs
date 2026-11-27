using System.ComponentModel;
using Starlight.Events;
using Starlight.Tools;

namespace Starlight.Tools.Pc;

/// <summary>The notify tool (plan P1): a Windows notification, for when the user may not be looking or listening.
/// Asks after outside content, so a web page can't make Starlight show its message as if it were Starlight's.</summary>
public sealed class NotifyTools(EventHub events) : IToolProvider
{
    public const int MaxTitle = 60, MaxText = 250; // what a Windows notification shows

    [Tool("notify", AskAfterUntrusted = true)]
    [Description("Shows a Windows notification. Use it when the user asks for a notification, or to tell them a long task is done while they may not be looking at the screen.")]
    public string Notify(
        [Description("The message, one or two short sentences")] string text,
        [Description("A short title. Optional; \"Starlight\" if not given.")] string? title = null)
    {
        var body = Clean(text, MaxText);
        if (body.Length == 0)
        {
            throw new ToolException("Give the text of the notification.");
        }
        var head = Clean(title ?? "", MaxTitle);
        events.Publish(new UserNotification(head.Length == 0 ? "Starlight" : head, body));
        return "Notification shown.";
    }

    internal static string Clean(string text, int max)
    {
        var one = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return one.Length > max ? one[..(max - 1)].TrimEnd() + "…" : one;
    }
}
