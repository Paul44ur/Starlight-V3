using System.Text;
using System.Text.RegularExpressions;

namespace Starlight.Voice;

/// <summary>Cuts a streamed answer into sentences, so each is spoken as soon as it is complete (plan 5.3).</summary>
public sealed class SentenceSplitter
{
    private readonly StringBuilder _buffer = new();

    /// <summary>Adds streamed text and returns the sentences that are now complete.</summary>
    public IEnumerable<string> Add(string delta)
    {
        _buffer.Append(delta);
        var text = _buffer.ToString();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var end = c is '.' or '!' or '?' or '\n' or ':' or ';';
            if (!end)
            {
                continue;
            }
            // A sentence ends at the mark only when whitespace follows (not "3.5" or "e.g.x").
            if (i + 1 >= text.Length || !char.IsWhiteSpace(text[i + 1]))
            {
                if (c != '\n')
                {
                    continue;
                }
            }
            var sentence = text[start..(i + 1)].Trim();
            if (c == '.' && IsAbbreviation(sentence))
            {
                continue;
            }
            if (sentence.Length >= 2 || c == '\n')
            {
                if (sentence.Length > 0)
                {
                    yield return sentence;
                }
                start = i + 1;
            }
        }
        _buffer.Remove(0, start);
    }

    /// <summary>What is left when the answer is complete.</summary>
    public string? Flush()
    {
        var rest = _buffer.ToString().Trim();
        _buffer.Clear();
        return rest.Length > 0 ? rest : null;
    }

    private static readonly string[] Abbreviations = ["e.g.", "i.e.", "etc.", "vs.", "mr.", "mrs.", "ms.", "dr.", "no.", "approx."];

    private static bool IsAbbreviation(string sentence)
    {
        var lower = sentence.ToLowerInvariant();
        return Abbreviations.Any(a => lower.EndsWith(" " + a, StringComparison.Ordinal) || lower == a)
            || Regex.IsMatch(sentence, @"(^|\s)\d+\.$"); // "1." in a list, "Step 2."
    }
}

/// <summary>Turns answer text into what should be said out loud.</summary>
public static partial class SpeechText
{
    public static string ForSpeech(string text)
    {
        var s = CodeBlock().Replace(text, " ");
        s = Link().Replace(s, "$1");
        s = Url().Replace(s, "a link");
        s = s.Replace("**", "", StringComparison.Ordinal).Replace("__", "", StringComparison.Ordinal)
             .Replace("`", "", StringComparison.Ordinal).Replace("#", "", StringComparison.Ordinal);
        s = Bullet().Replace(s, "");
        s = Spaces().Replace(s, " ");
        return s.Trim();
    }

    /// <summary>Whisper invents text on silence or noise. These results are dropped.</summary>
    public static bool IsNoise(string heard)
    {
        var t = heard.Trim().Trim('.', '!', ' ').ToLowerInvariant();
        return t.Length == 0
            || t.StartsWith('[') || t.StartsWith('(')
            || t is "thank you" or "thanks for watching" or "you" or "bye" or "thank you for watching" or "subscribe";
    }

    [GeneratedRegex(@"```.*?```", RegexOptions.Singleline)]
    private static partial Regex CodeBlock();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]+\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"https?://\S+?(?=[.,!?;:)]?(\s|$))")]
    private static partial Regex Url();

    [GeneratedRegex(@"(?m)^\s*([-*+]|\d+\.)\s+")]
    private static partial Regex Bullet();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
