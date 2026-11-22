namespace Starlight.Tools.Network;

/// <summary>Which SSH commands run without asking: a command that matches a pattern of the server's allow-list
/// ("*" is any text) and has no shell tricks (chaining, pipes, redirects, substitution).</summary>
public static class CommandPolicy
{
    private static readonly char[] ShellChars = [';', '|', '&', '`', '$', '(', ')', '<', '>', '\n', '\r', '\\', '"', '\''];

    public static bool IsAllowed(string command, IEnumerable<string> patterns)
    {
        var c = string.Join(' ', command.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (c.Length == 0 || c.IndexOfAny(ShellChars) >= 0)
        {
            return false;
        }
        return patterns.Any(p => Matches(string.Join(' ', p.Split(' ', StringSplitOptions.RemoveEmptyEntries)), c));
    }

    private static bool Matches(string pattern, string command)
    {
        var parts = pattern.Split('*');
        if (parts.Length == 1)
        {
            return pattern == command;
        }
        if (!command.StartsWith(parts[0], StringComparison.Ordinal) || !command.EndsWith(parts[^1], StringComparison.Ordinal))
        {
            return false;
        }
        var pos = parts[0].Length;
        for (var i = 1; i < parts.Length - 1; i++)
        {
            var at = command.IndexOf(parts[i], pos, StringComparison.Ordinal);
            if (at < 0)
            {
                return false;
            }
            pos = at + parts[i].Length;
        }
        return command.Length - parts[^1].Length >= pos;
    }
}
