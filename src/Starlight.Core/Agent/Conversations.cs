using Starlight.Storage;

namespace Starlight.Agent;

/// <summary>
/// One shared object per conversation, so voice and the chat window add to the same one: a conversation
/// started by voice can be continued by typing, and the other way round.
/// </summary>
public sealed class Conversations(StarlightDb db, TimeProvider? time = null)
{
    public static readonly TimeSpan VoiceContinueWindow = TimeSpan.FromMinutes(10);

    private readonly Dictionary<string, Conversation> _open = [];
    private readonly Lock _gate = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private DateTimeOffset _lastUsed = DateTimeOffset.MinValue;

    /// <summary>The conversation used last, by voice or chat.</summary>
    public Conversation? Current { get; private set; }

    /// <summary>Raised when the current conversation changes (the chat window follows it).</summary>
    public event Action<Conversation>? CurrentChanged;

    public Conversation Get(string id)
    {
        lock (_gate)
        {
            if (_open.TryGetValue(id, out var c))
            {
                return c;
            }
            var info = db.ListConversations().FirstOrDefault(x => x.Id == id);
            var messages = db.LoadMessages(id) ?? [];
            c = new Conversation(id, info?.Title, messages, info?.Created);
            _open[id] = c;
            return c;
        }
    }

    public Conversation New()
    {
        var c = new Conversation();
        lock (_gate)
        {
            _open[c.Id] = c;
        }
        return c;
    }

    /// <summary>Marks a conversation as the one in use.</summary>
    public void Use(Conversation c)
    {
        var changed = !ReferenceEquals(Current, c);
        Current = c;
        _lastUsed = _time.GetUtcNow();
        if (changed)
        {
            CurrentChanged?.Invoke(c);
        }
    }

    /// <summary>For a spoken request: the current conversation if it was used in the last 10 minutes, else a new one.</summary>
    public Conversation ForVoice()
    {
        var c = Current is not null && _time.GetUtcNow() - _lastUsed < VoiceContinueWindow ? Current : New();
        Use(c);
        return c;
    }

    public void Forget(string id)
    {
        lock (_gate)
        {
            _open.Remove(id);
        }
        if (Current?.Id == id)
        {
            Current = null;
        }
    }
}
