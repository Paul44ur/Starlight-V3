using Microsoft.Extensions.AI;

namespace Starlight.Agent;

/// <summary>A conversation: the messages after the system prompt. The system prompt is rebuilt every turn.</summary>
public sealed class Conversation
{
    public Conversation(string? id = null, string? title = null, List<ChatMessage>? messages = null, DateTimeOffset? created = null)
    {
        Id = id ?? Guid.NewGuid().ToString("N");
        Title = title ?? "New conversation";
        Messages = messages ?? [];
        Created = created ?? DateTimeOffset.Now;
    }

    public string Id { get; }
    public string Title { get; set; }
    public DateTimeOffset Created { get; }
    public List<ChatMessage> Messages { get; }

    /// <summary>Rough token count (3.5 characters a token), enough to decide when to summarize.</summary>
    public int EstimateTokens() => (int)(Messages.Sum(m => Size(m)) / 3.5);

    internal static int Size(ChatMessage m) =>
        m.Contents.Sum(c => c switch
        {
            TextContent t => t.Text.Length,
            FunctionCallContent f => f.Name.Length + (f.Arguments?.Sum(a => a.Key.Length + (a.Value?.ToString()?.Length ?? 0)) ?? 0),
            FunctionResultContent r => r.Result?.ToString()?.Length ?? 0,
            DataContent d => 1500 * 4, // an image costs roughly 1,500 tokens
            _ => 100,
        });
}

/// <summary>Adds a block of context to the system prompt for a turn (memories, skills, the screen).</summary>
public interface IPromptContributor
{
    Task<string?> ContributeAsync(string userText, CancellationToken ct);
}

/// <summary>Runs after a turn (memory extraction, statistics). Must not throw into the turn.</summary>
public interface ITurnObserver
{
    Task OnTurnCompletedAsync(Conversation conversation, string userText, string answer, CancellationToken ct);
}
