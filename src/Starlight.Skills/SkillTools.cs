using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Starlight.Agent;
using Starlight.Tools;

namespace Starlight.Skills;

/// <summary>skill_run (plan 5.10): runs an approved skill's steps through the dispatcher, so each step's own
/// permission still applies. The skills are listed in the prompt.</summary>
public sealed class SkillTools(SkillStore store) : IToolProvider, IPromptContributor
{
    /// <summary>Set by the app: the dispatcher, so steps run with their permissions and audit.</summary>
    public IToolCaller? Caller { get; set; }

    [Tool("skill_run", AskAfterUntrusted = true, TimeoutSeconds = 300)]
    [Description("Runs a skill the user approved, by name, with values for its parameters. The skills are listed in your instructions.")]
    public async Task<string> Run(
        [Description("The skill's name")] string name,
        [Description("Values for the skill's parameters, by parameter name")] Dictionary<string, string>? values = null,
        CancellationToken ct = default)
    {
        var skill = store.Find(name);
        if (skill is null)
        {
            var all = store.All();
            return all.Count == 0 ? "There are no skills yet." : $"No skill named \"{name}\". Skills: {string.Join(", ", all.Select(s => s.Name))}.";
        }
        values ??= [];
        var missing = skill.Parameters.Where(p => !values.ContainsKey(p)).ToList();
        if (missing.Count > 0)
        {
            return $"{skill.Name} needs: {string.Join(", ", missing)}. Ask the user for them.";
        }
        var caller = Caller ?? throw new InvalidOperationException("SkillTools.Caller is not set.");
        var sb = new StringBuilder();
        for (var i = 0; i < skill.Steps.Count; i++)
        {
            var step = skill.Steps[i];
            var args = Fill(step.ArgumentsJson, values);
            var result = await caller.CallAsync(step.Tool, args, ct);
            sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"{i + 1}. {step.Tool}: {(result.Ok ? "ok" : "failed")} - {Short(result.Text)}\n");
            if (!result.Ok)
            {
                sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"Stopped at step {i + 1} of {skill.Steps.Count}.");
                return sb.ToString();
            }
        }
        sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"{skill.Name} done ({skill.Steps.Count} steps).");
        return sb.ToString();
    }

    public Task<string?> ContributeAsync(string userText, CancellationToken ct)
    {
        var skills = store.All();
        if (skills.Count == 0)
        {
            return Task.FromResult<string?>(null);
        }
        var sb = new StringBuilder("Skills the user approved (run with skill_run):");
        foreach (var s in skills.Take(25))
        {
            sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"\n- {s.Name}({string.Join(", ", s.Parameters)}): first made for \"{Short(s.Description)}\"");
        }
        return Task.FromResult<string?>(sb.ToString());
    }

    /// <summary>Puts the values in place of {name}; values are JSON-escaped so they can't change the arguments' shape.</summary>
    internal static Dictionary<string, object?> Fill(string argumentsJson, IReadOnlyDictionary<string, string> values)
    {
        var json = argumentsJson;
        foreach (var (k, v) in values)
        {
            var escaped = JsonSerializer.Serialize(v)[1..^1];
            json = json.Replace("{" + k + "}", escaped, StringComparison.Ordinal);
        }
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
    }

    private static string Short(string s)
    {
        var line = s.ReplaceLineEndings(" ");
        return line.Length <= 140 ? line : line[..140] + "…";
    }
}
