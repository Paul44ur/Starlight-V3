namespace Starlight.Tools.Web;

/// <summary>
/// The rules of a robots.txt for Starlight: the group for "starlight" if there is one, else the group for "*".
/// The longest matching Allow or Disallow wins; "*" and "$" work as in Google's rules.
/// </summary>
public sealed class RobotsRules
{
    public const string Agent = "starlight";
    private readonly List<(bool Allow, string Pattern)> _rules;

    private RobotsRules(List<(bool, string)> rules) => _rules = rules;

    public static RobotsRules AllowAll { get; } = new([]);

    public static RobotsRules Parse(string text)
    {
        var groups = new List<(List<string> Agents, List<(bool, string)> Rules)>();
        List<string>? agents = null;
        List<(bool, string)>? rules = null;
        var lastWasAgent = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Split('#')[0].Trim();
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }
            var key = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();
            if (key == "user-agent")
            {
                if (!lastWasAgent || agents is null)
                {
                    agents = [];
                    rules = [];
                    groups.Add((agents, rules));
                }
                agents.Add(value.ToLowerInvariant());
                lastWasAgent = true;
            }
            else if ((key == "allow" || key == "disallow") && rules is not null)
            {
                lastWasAgent = false;
                if (value.Length > 0)
                {
                    rules.Add((key == "allow", value));
                }
            }
            else
            {
                lastWasAgent = false;
            }
        }
        var mine = groups.Where(g => g.Agents.Any(a => a == Agent || a.StartsWith(Agent + "/", StringComparison.Ordinal))).ToList();
        var chosen = mine.Count > 0 ? mine : groups.Where(g => g.Agents.Contains("*")).ToList();
        return new RobotsRules(chosen.SelectMany(g => g.Rules).ToList());
    }

    public bool Allows(string pathAndQuery)
    {
        var path = string.IsNullOrEmpty(pathAndQuery) ? "/" : pathAndQuery;
        var best = -1;
        var allowed = true;
        foreach (var (allow, pattern) in _rules)
        {
            if (Matches(pattern, path) && (pattern.Length > best || pattern.Length == best && allow))
            {
                best = pattern.Length;
                allowed = allow;
            }
        }
        return allowed;
    }

    private static bool Matches(string pattern, string path)
    {
        var anchored = pattern.EndsWith('$');
        if (anchored)
        {
            pattern = pattern[..^1];
        }
        var parts = pattern.Split('*');
        var pos = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (i == 0)
            {
                if (!path.StartsWith(part, StringComparison.Ordinal))
                {
                    return false;
                }
                pos = part.Length;
                continue;
            }
            var found = path.IndexOf(part, pos, StringComparison.Ordinal);
            if (found < 0)
            {
                return false;
            }
            pos = found + part.Length;
        }
        if (!anchored)
        {
            return true;
        }
        // With "$" the last part must end the path.
        return parts.Length == 1 ? path.Length == pos : path.EndsWith(parts[^1], StringComparison.Ordinal);
    }
}
