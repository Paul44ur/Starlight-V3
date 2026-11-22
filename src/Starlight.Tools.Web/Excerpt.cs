namespace Starlight.Tools.Web;

/// <summary>Picks the paragraphs of a page that share the most words with the question, in page order.</summary>
public static class Excerpt
{
    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "is", "are", "was", "were", "of", "to", "in", "on", "for", "and", "or", "what", "which", "who", "how", "why", "when", "does", "do", "did", "with", "about", "from", "by", "it", "this", "that", "be", "best",
    };

    public static string Best(string text, string question, int maxChars)
    {
        if (text.Length <= maxChars)
        {
            return text;
        }
        var words = Words(question).Where(w => !Stop.Contains(w)).Select(Stem).ToHashSet();
        var paragraphs = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select((p, i) => (Text: p, Index: i, Score: Score(p, words)))
            .ToList();
        var chosen = new List<(string Text, int Index)>();
        var used = 0;
        foreach (var p in paragraphs.Where(p => p.Text.Length >= 40).OrderByDescending(p => p.Score).ThenBy(p => p.Index))
        {
            if (p.Score == 0 && chosen.Count > 0)
            {
                break;
            }
            var piece = p.Text.Length > maxChars / 2 ? p.Text[..(maxChars / 2)] + "…" : p.Text;
            if (used + piece.Length > maxChars)
            {
                continue;
            }
            chosen.Add((piece, p.Index));
            used += piece.Length + 1;
        }
        return chosen.Count == 0 ? text[..maxChars] + "…" : string.Join('\n', chosen.OrderBy(c => c.Index).Select(c => c.Text));
    }

    private static double Score(string paragraph, HashSet<string> words)
    {
        if (words.Count == 0)
        {
            return 0;
        }
        var have = Words(paragraph).Select(Stem).ToHashSet();
        return words.Count(have.Contains) / (double)words.Count;
    }

    private static IEnumerable<string> Words(string s) =>
        new string(s.Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ').ToArray()).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static string Stem(string w) => w.Length > 4 && w.EndsWith('s') ? w[..^1] : w;
}
