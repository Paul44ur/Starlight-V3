using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text;
using Starlight.Agent;
using Starlight.Events;
using Starlight.Tools;

namespace Starlight.Memory;

/// <summary>The three memory tools (plan 5.7) and the prompt block with the facts that fit the request.</summary>
public sealed class MemoryTools(MemoryStore store) : IToolProvider, IPromptContributor
{
    [Tool("memory_remember", AskAfterUntrusted = true)]
    [Description("Remembers a lasting fact about the user or their world, when they tell you something worth keeping (\"my sister's birthday is 3 March\"). A fact on the same topic replaces the old one.")]
    public string Remember(
        [Description("The fact as one clear sentence, for example \"The user's sister Anna has her birthday on 3 March.\"")] string text,
        [Description("A short topic that identifies it, for example \"sister birthday\". A new fact on this topic replaces the old one.")] string? topic = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ToolException("Give the fact to remember.");
        }
        var (saved, replaced) = store.Remember(text, topic, ToolCallContext.Current is { } ctx ? ConversationOf(ctx) : null);
        return replaced is null ? $"Remembered: {saved.Text}" : $"Remembered: {saved.Text} (replaces: {replaced.Text})";
    }

    [Tool("memory_recall")]
    [Description("Looks up what you remember about something.")]
    public string Recall([Description("What to look up, in a few words")] string query)
    {
        var facts = store.Search(query, 10);
        return facts.Count == 0 ? $"Nothing remembered about \"{query}\"." : string.Join('\n', facts.Select(f => $"#{f.Id}: {f.Text} ({f.Updated:d MMM yyyy})"));
    }

    [Tool("memory_forget", Permission.Ask)]
    [Description("Forgets remembered facts. Give the fact number from memory_recall, or words to find them.")]
    public string Forget([Description("A fact number like 12, or words that find the facts to forget")] string what)
    {
        var trimmed = what.Trim().TrimStart('#');
        List<Fact> facts = long.TryParse(trimmed, out var id)
            ? store.All().Where(f => f.Id == id).ToList()
            : store.Search(what, 5);
        if (facts.Count == 0)
        {
            return $"Nothing remembered matches \"{what}\".";
        }
        foreach (var f in facts)
        {
            store.Forget(f.Id);
        }
        return "Forgot: " + string.Join("; ", facts.Select(f => f.Text));
    }

    public Task<string?> ContributeAsync(string userText, CancellationToken ct)
    {
        var facts = store.Search(userText, 5);
        if (facts.Count == 0)
        {
            return Task.FromResult<string?>(null);
        }
        var sb = new StringBuilder("What you remember that may matter here:");
        foreach (var f in facts)
        {
            sb.Append("\n- ").Append(f.Text);
        }
        return Task.FromResult<string?>(sb.ToString());
    }

    private readonly ConcurrentDictionary<string, string> _turns = new();

    /// <summary>Follows which conversation each turn belongs to, so facts know where they were learned
    /// and "forget this conversation" removes them.</summary>
    public IDisposable TrackTurns(EventHub events) => events.Subscribe<TurnStarted>(e =>
    {
        if (_turns.Count > 200)
        {
            _turns.Clear();
        }
        _turns[e.TurnId] = e.ConversationId;
    });

    private string? ConversationOf(ToolCallContext ctx) => _turns.TryGetValue(ctx.TurnId, out var c) ? c : null;
}
