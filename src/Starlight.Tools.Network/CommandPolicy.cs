namespace Starlight.Tools.Network;

/// <summary>Which SSH commands run without asking: a command that matches a pattern of the server's allow-list
/// and has no shell tricks (chaining, pipes, redirects, substitution, globs). A "*" in a pattern stands for any
/// text, but not for an option ("-f", "--force") and not for "..": "systemctl status *" lets the user's
/// command name a service, not add flags or climb out of a folder.</summary>
public static class CommandPolicy
{
    private static readonly System.Buffers.SearchValues<char> ShellChars = System.Buffers.SearchValues.Create(";|&`$()<>\n\r\\\"'*?[{~");

    public static bool IsAllowed(string command, IEnumerable<string> patterns)
    {
        var c = string.Join(' ', command.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        // Control characters (a tab is a separator for the remote shell) never pass.
        if (c.Length == 0 || c.AsSpan().IndexOfAny(ShellChars) >= 0 || c.Any(char.IsControl))
        {
            return false;
        }
        return patterns.Any(p => Matches(string.Join(' ', p.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Split('*'), 0, c, 0));
    }

    /// <summary>Matches parts[i..] against command[pos..], where a "*" sits between each two parts.</summary>
    private static bool Matches(string[] parts, int i, string command, int pos)
    {
        if (i == 0)
        {
            if (!command.AsSpan(pos).StartsWith(parts[0], StringComparison.Ordinal))
            {
                return false;
            }
            return parts.Length == 1 ? command.Length == parts[0].Length : Matches(parts, 1, command, parts[0].Length);
        }
        // Try every place the next literal part can start; the text skipped is what the "*" covers.
        var last = i == parts.Length - 1;
        for (var at = pos; at <= command.Length; at++)
        {
            if (last ? command.Length - at != parts[i].Length || !command.AsSpan(at).SequenceEqual(parts[i]) : !command.AsSpan(at).StartsWith(parts[i], StringComparison.Ordinal))
            {
                continue;
            }
            if (!SafeWildcard(command[pos..at]))
            {
                continue;
            }
            if (last || Matches(parts, i + 1, command, at + parts[i].Length))
            {
                return true;
            }
        }
        return false;
    }

    private static bool SafeWildcard(string covered) =>
        !covered.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(t => t.StartsWith('-'))
        && !covered.Contains("..", StringComparison.Ordinal);
}
