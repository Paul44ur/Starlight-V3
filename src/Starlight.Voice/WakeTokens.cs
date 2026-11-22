namespace Starlight.Voice;

/// <summary>
/// Turns a wake phrase typed in Settings ("Hey Starlight") into the token line sherpa-onnx keyword spotting reads,
/// by greedy longest match over the model's BPE pieces. Plain logic, tested in the container.
/// </summary>
public static class WakeTokens
{
    public const char WordStart = '▁'; // ▁, the BPE word-start mark

    /// <summary>"▁HE Y ▁STAR LIGHT :2.0 #0.25 @HEY_STARLIGHT"-style line, or null if the phrase has letters the model lacks.</summary>
    public static string? Line(string phrase, IReadOnlySet<string> vocab, double boost = 1.5, double threshold = 0.25)
    {
        var words = new string(phrase.ToUpperInvariant().Select(c => char.IsLetter(c) || c == '\'' ? c : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return null;
        }
        var upper = vocab.Any(v => v.Any(char.IsUpper));
        var pieces = new List<string>();
        foreach (var raw in words)
        {
            var word = upper ? raw : raw.ToLowerInvariant();
            var text = WordStart + word;
            var i = 0;
            while (i < text.Length)
            {
                var found = false;
                for (var len = text.Length - i; len > 0; len--)
                {
                    var piece = text.Substring(i, len);
                    if (vocab.Contains(piece))
                    {
                        pieces.Add(piece);
                        i += len;
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    if (i == 0 && vocab.Contains(WordStart.ToString()))
                    {
                        pieces.Add(WordStart.ToString());
                        i = 1;
                        continue;
                    }
                    return null;
                }
            }
        }
        var label = string.Join('_', words).ToUpperInvariant();
        return FormattableString.Invariant($"{string.Join(' ', pieces)} :{boost:0.0#} #{threshold:0.0#} @{label}");
    }

    /// <summary>Token pieces from a sherpa tokens.txt ("piece id" per line).</summary>
    public static HashSet<string> ReadVocab(IEnumerable<string> lines) =>
        lines.Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "")
             .Where(p => p.Length > 0 && !p.StartsWith('<'))
             .ToHashSet(StringComparer.Ordinal);

    /// <summary>Sensitivity 0 (strict) to 1 (eager) → sherpa's keyword threshold (lower = fires more easily).</summary>
    public static double Threshold(double sensitivity) => Math.Round(0.45 - 0.35 * Math.Clamp(sensitivity, 0, 1), 2);
}
