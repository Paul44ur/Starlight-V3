using System.Text;
using System.Text.RegularExpressions;

namespace Starlight.Voice;

/// <summary>Cuts a streamed answer into sentences, so each is spoken as soon as it is complete (plan 5.3).</summary>
public sealed class SentenceSplitter
{
    private readonly StringBuilder _buffer = new();
    private bool _inCode;

    /// <summary>Adds streamed text and returns the sentences that are now complete.
    /// A fenced code block (```) is never spoken: it is dropped as it streams.</summary>
    public IEnumerable<string> Add(string delta)
    {
        _buffer.Append(delta);
        var result = new List<string>();
        while (true)
        {
            var text = _buffer.ToString();
            if (_inCode)
            {
                var close = text.IndexOf("```", StringComparison.Ordinal);
                if (close < 0)
                {
                    // Keep only trailing backticks: they may start the closing fence.
                    var keep = text.Length - text.TrimEnd('`').Length;
                    _buffer.Clear().Append(text[^Math.Min(keep, 2)..]);
                    return result;
                }
                _buffer.Remove(0, close + 3);
                _inCode = false;
                continue;
            }
            var open = text.IndexOf("```", StringComparison.Ordinal);
            // Text that ends in a backtick or two may become a fence: wait for more.
            var limit = open >= 0 ? open : text.Length - (text.Length - text.TrimEnd('`').Length);
            var start = Split(text, limit, result);
            if (open < 0)
            {
                _buffer.Remove(0, start);
                return result;
            }
            var before = text[start..open].Trim();
            if (before.Length > 0)
            {
                result.Add(before);
            }
            _buffer.Remove(0, open + 3);
            _inCode = true;
        }
    }

    private static int Split(string text, int limit, List<string> result)
    {
        var start = 0;
        for (var i = 0; i < limit; i++)
        {
            var c = text[i];
            var end = c is '.' or '!' or '?' or '\n' or ':' or ';';
            if (!end)
            {
                continue;
            }
            // A sentence ends at the mark only when whitespace follows (not "3.5" or "e.g.x").
            if (i + 1 >= limit || !char.IsWhiteSpace(text[i + 1]))
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
                    result.Add(sentence);
                }
                start = i + 1;
            }
        }
        return start;
    }

    /// <summary>What is left when the answer is complete.</summary>
    public string? Flush()
    {
        var rest = _inCode ? "" : _buffer.ToString().Trim();
        _buffer.Clear();
        _inCode = false;
        return rest.Length > 0 ? rest : null;
    }

    private static readonly string[] Abbreviations = ["e.g.", "i.e.", "etc.", "vs.", "mr.", "mrs.", "ms.", "dr.", "approx."];

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
        s = Image().Replace(s, " ");
        s = Link().Replace(s, "$1");
        s = Url().Replace(s, "a link");
        s = s.Replace("**", "", StringComparison.Ordinal).Replace("__", "", StringComparison.Ordinal)
             .Replace("`", "", StringComparison.Ordinal);
        s = Heading().Replace(s, "");
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

    [GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)")]
    private static partial Regex Image();

    [GeneratedRegex(@"(?m)^\s*#{1,6}\s*")]
    private static partial Regex Heading();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]+\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"https?://\S+?(?=[.,!?;:)]?(\s|$))")]
    private static partial Regex Url();

    [GeneratedRegex(@"(?m)^\s*([-*+]|\d+\.)\s+")]
    private static partial Regex Bullet();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}

/// <summary>Spoken answers to a Yes/No card.</summary>
public static class VoiceApproval
{
    public static Starlight.Tools.ApprovalAnswer? Parse(string heard)
    {
        // Whisper writes "Yes, please." or "Don’t." (curly apostrophe): drop the punctuation, keep the words.
        var question = heard.TrimEnd().EndsWith('?');
        var words = heard.Replace('\u2019', '\'').ToLowerInvariant()
            .Split([' ', ',', '.', '!', '?', ';', ':'], StringSplitOptions.RemoveEmptyEntries);
        var t = string.Join(' ', words);
        // Only a clear phrase gives a lasting yes: "always" at the start of a longer sentence
        // ("always ask me first") must not.
        string[] always = ["always", "always allow", "always allow it", "always allow this", "always yes", "yes always"];
        if (always.Contains(t) && !question)
        {
            return Starlight.Tools.ApprovalAnswer.Always;
        }
        string[] yes = ["yes", "yeah", "yep", "sure", "ok", "okay", "do it", "go ahead", "yes please", "yes do it", "yes go ahead", "okay do it", "sure go ahead"];
        string[] no = ["no", "nope", "don't", "do not", "cancel", "stop", "no thanks", "no thank you", "no don't", "don't do it", "no stop"];
        if (yes.Contains(t))
        {
            return Starlight.Tools.ApprovalAnswer.Yes;
        }
        return no.Contains(t) ? Starlight.Tools.ApprovalAnswer.No : null;
    }
}

/// <summary>A spoken code such as "four two seven, one nine nine" as digits; null if it is not a code.</summary>
public static class SpokenCode
{
    private static readonly Dictionary<string, char> Digits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zero"] = '0', ["oh"] = '0', ["o"] = '0', ["one"] = '1', ["two"] = '2', ["to"] = '2', ["too"] = '2', ["three"] = '3', ["four"] = '4', ["for"] = '4',
        ["five"] = '5', ["six"] = '6', ["seven"] = '7', ["eight"] = '8', ["nine"] = '9',
    };

    public static string? Parse(string heard)
    {
        var sb = new System.Text.StringBuilder();
        var words = new string(heard.Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ').ToArray()).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var w in words)
        {
            if (w.All(char.IsDigit))
            {
                sb.Append(w);
            }
            else if (Digits.TryGetValue(w, out var d))
            {
                sb.Append(d);
            }
            else if (sb.Length > 0 || !w.Equals("code", StringComparison.OrdinalIgnoreCase) && !w.Equals("is", StringComparison.OrdinalIgnoreCase) && !w.Equals("the", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }
        return sb.Length is >= 4 and <= 10 ? sb.ToString() : null;
    }
}
