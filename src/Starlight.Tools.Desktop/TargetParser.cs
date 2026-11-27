using System.Globalization;
using System.Text.RegularExpressions;

namespace Starlight.Tools.Desktop;

/// <summary>What the model named as a target: an element number, screen coordinates, or a name. Plain logic, tested in the container.</summary>
public sealed record TargetSpec(int? Element, (int X, int Y)? Point, string? Name);

/// <summary>Where the last screen_look picture sits on the desktop and how much it was shrunk.</summary>
public sealed record PictureFrame(Starlight.Events.ScreenRect Area, double Scale);

public static partial class TargetParser
{
    /// <summary>How much a picture is shrunk so its longer side is at most <paramref name="maxSide"/>.</summary>
    public static double PictureScale(int width, int height, int maxSide) =>
        Math.Min(1.0, (double)maxSide / Math.Max(1, Math.Max(width, height)));

    /// <summary>
    /// The model reads x,y off the picture it was shown, which starts at the window's corner and may
    /// be shrunk. This turns them back into physical screen pixels.
    /// </summary>
    public static (int X, int Y) PictureToScreen(int x, int y, PictureFrame frame) =>
        (frame.Area.X + (int)Math.Round(x / frame.Scale), frame.Area.Y + (int)Math.Round(y / frame.Scale));

    public static TargetSpec Parse(string target)
    {
        var t = target.Trim();
        var number = ElementPattern().Match(t);
        if (number.Success)
        {
            return new TargetSpec(int.Parse(number.Groups[1].Value, CultureInfo.InvariantCulture), null, null);
        }
        var point = PointPattern().Match(t);
        if (point.Success)
        {
            return new TargetSpec(null, (int.Parse(point.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(point.Groups[2].Value, CultureInfo.InvariantCulture)), null);
        }
        return new TargetSpec(null, null, t.Trim('"', '\''));
    }

    /// <summary>Scores how well a control's name matches what was asked: 3 exact, 2 starts with, 1 contains all words, 0 no.</summary>
    public static int Match(string wanted, string name)
    {
        var w = AppMatcher.Normalize(wanted);
        var n = AppMatcher.Normalize(name);
        if (w.Length == 0 || n.Length == 0)
        {
            return 0;
        }
        if (n == w)
        {
            return 3;
        }
        if (n.StartsWith(w, StringComparison.Ordinal))
        {
            return 2;
        }
        return w.Split(' ').All(word => n.Contains(word, StringComparison.Ordinal)) ? 1 : 0;
    }

    [GeneratedRegex(@"^(?:\[|#|element\s*|number\s*)?(\d{1,3})\]?$", RegexOptions.IgnoreCase)]
    private static partial Regex ElementPattern();

    [GeneratedRegex(@"^\(?\s*(-?\d{1,5})\s*[,;x ]\s*(-?\d{1,5})\s*\)?$")]
    private static partial Regex PointPattern();
}
