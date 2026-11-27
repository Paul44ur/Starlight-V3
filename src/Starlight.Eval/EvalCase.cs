using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Starlight.Eval;

/// <summary>One eval case: something the user says and what the model should do first.</summary>
public sealed class EvalCase
{
    public string Id { get; set; } = "";
    public string Say { get; set; } = "";
    /// <summary>tool: call Tool with Args; none: answer without a tool; clarify: ask a question back.</summary>
    public string Expect { get; set; } = "tool";
    /// <summary>Tool name; several allowed with | ("web_search|web_research").</summary>
    public string? Tool { get; set; }
    /// <summary>Expected arguments. A value starting with ~ must be contained (case-insensitive); else equal.</summary>
    public Dictionary<string, string>? Args { get; set; }
    public string Source { get; set; } = "voice";

    [JsonIgnore]
    public IEnumerable<string> Tools => (Tool ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static List<EvalCase> Load(string file) =>
        JsonSerializer.Deserialize<List<EvalCase>>(File.ReadAllText(file), Json) ?? [];

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}

public sealed record EvalVerdict(bool Pass, string Why);

/// <summary>Scores the model's first reply against a case. Plain logic, tested in the container.</summary>
public static class EvalScorer
{
    public static EvalVerdict Score(EvalCase c, ChatResponse response)
    {
        var call = response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().FirstOrDefault();
        var text = response.Text ?? "";
        switch (c.Expect)
        {
            case "none":
                return call is null ? new(true, "answered") : new(false, $"called {call.Name} but no tool was needed");
            case "clarify":
                if (call is not null)
                {
                    return new(false, $"called {call.Name} instead of asking");
                }
                return text.Contains('?') ? new(true, "asked back") : new(false, "answered without asking what was meant");
        }
        if (call is null)
        {
            return new(false, $"no tool call; said: {Clip(text)}");
        }
        if (!c.Tools.Contains(call.Name, StringComparer.OrdinalIgnoreCase))
        {
            return new(false, $"called {call.Name}, expected {c.Tool}");
        }
        foreach (var (key, want) in c.Args ?? [])
        {
            if (call.Arguments is null || !call.Arguments.TryGetValue(key, out var gotObj) || gotObj is null)
            {
                return new(false, $"{call.Name} without {key}");
            }
            var got = gotObj is JsonElement { ValueKind: JsonValueKind.String } e ? e.GetString() ?? "" : gotObj.ToString() ?? "";
            var ok = want.StartsWith('~')
                ? got.Contains(want[1..], StringComparison.OrdinalIgnoreCase)
                : string.Equals(got.Trim(), want, StringComparison.OrdinalIgnoreCase);
            if (!ok)
            {
                return new(false, $"{call.Name} {key}='{Clip(got)}', expected '{want}'");
            }
        }
        return new(true, $"{call.Name} {JsonSerializer.Serialize(call.Arguments)}");
    }

    private static string Clip(string s) => s.Length <= 80 ? s : s[..80] + "…";
}

/// <summary>The 1.0 gate (plan): 90 % of all cases, and 80 % for each tool that has enough cases to judge.</summary>
public static class EvalGate
{
    public const double Overall = 90, PerTool = 80;
    public const int MinCasesPerTool = 3;

    /// <summary>Why the run fails the gate, or null if it passes.</summary>
    public static string? Failure(IReadOnlyCollection<(string Group, bool Pass)> results)
    {
        if (results.Count == 0)
        {
            return "no cases ran";
        }
        var rate = 100.0 * results.Count(r => r.Pass) / results.Count;
        if (rate < Overall)
        {
            return $"{rate:0} % overall, below {Overall:0} %";
        }
        foreach (var g in results.GroupBy(r => r.Group).Where(g => g.Count() >= MinCasesPerTool))
        {
            var r = 100.0 * g.Count(x => x.Pass) / g.Count();
            if (r < PerTool)
            {
                return $"{g.Key} at {r:0} %, below {PerTool:0} %";
            }
        }
        return null;
    }
}
