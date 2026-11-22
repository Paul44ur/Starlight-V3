using System.Globalization;
using System.Text.RegularExpressions;

namespace Starlight.Tools.Desktop;

/// <summary>What the model named as a target: an element number, screen coordinates, or a name. Plain logic, tested in the container.</summary>
public sealed record TargetSpec(int? Element, (int X, int Y)? Point, string? Name);

public static partial class TargetParser
{
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
