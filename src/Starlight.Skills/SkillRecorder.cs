using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Starlight.Events;

namespace Starlight.Skills;

/// <summary>
/// Watches turns. When a turn did a task in two or more successful tool steps, it saves a draft skill and offers it;
/// the user approves it in the chat window (plan 5.10). Values from the request become parameters.
/// </summary>
public sealed class SkillRecorder : IDisposable
{
    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
    {
        "skill_run", "memory_remember", "memory_recall", "memory_forget", "code_write", "web_login", "server_list",
    };

    private readonly SkillStore _store;
    private readonly EventHub _events;
    private readonly ConcurrentDictionary<string, Turn> _turns = new();
    private readonly List<IDisposable> _subs = [];

    private sealed class Turn(string conversation, string request)
    {
        public string Conversation { get; } = conversation;
        public string Request { get; } = request;
        public List<(string Tool, string Args)> Started { get; } = [];
        public List<(string Tool, string Args)> Steps { get; } = [];
        public bool Failed { get; set; }
    }

    public SkillRecorder(SkillStore store, EventHub events)
    {
        _store = store;
        _events = events;
        _subs.Add(events.Subscribe<TurnStarted>(e => _turns[e.TurnId] = new Turn(e.ConversationId, e.Text)));
        _subs.Add(events.Subscribe<ToolStarted>(e =>
        {
            if (_turns.TryGetValue(e.TurnId, out var t))
            {
                lock (t)
                {
                    t.Started.Add((e.Tool, e.ArgumentsJson));
                }
            }
        }));
        _subs.Add(events.Subscribe<ToolFinished>(e =>
        {
            if (!_turns.TryGetValue(e.TurnId, out var t))
            {
                return;
            }
            lock (t)
            {
                var i = t.Started.FindIndex(s => s.Tool == e.Tool);
                if (i < 0)
                {
                    return;
                }
                var step = t.Started[i];
                t.Started.RemoveAt(i);
                if (!e.Ok)
                {
                    t.Failed = true;
                }
                else if (!Skip.Contains(e.Tool))
                {
                    t.Steps.Add(step);
                }
                if (e.Tool == "skill_run")
                {
                    t.Failed = true; // a turn that ran a skill is not offered again
                }
            }
        }));
        _subs.Add(events.Subscribe<AnswerCompleted>(e =>
        {
            if (_turns.TryRemove(e.TurnId, out var t) && !e.Failed)
            {
                Offer(t);
            }
        }));
    }

    private void Offer(Turn t)
    {
        List<(string Tool, string Args)> steps;
        lock (t)
        {
            if (t.Failed || t.Steps.Count < 2)
            {
                return;
            }
            steps = t.Steps.ToList();
        }
        var (templated, parameters) = Parameterize(steps, t.Request);
        var shape = Shape(templated);
        if (_store.All().Any(s => Shape(s.Steps) == shape) || _store.Drafts().Any(d => Shape(d.Steps) == shape))
        {
            return;
        }
        var id = _store.AddDraft(t.Conversation, t.Request, templated, parameters);
        _events.Publish(new SkillOffered(id, t.Conversation, SuggestName(t.Request)));
    }

    /// <summary>String values that the user typed in the request become parameters named after their argument.</summary>
    internal static (List<SkillStep> Steps, List<string> Parameters) Parameterize(IEnumerable<(string Tool, string Args)> steps, string request)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // value -> parameter
        var result = new List<SkillStep>();
        foreach (var (tool, args) in steps)
        {
            JsonObject obj;
            try
            {
                obj = JsonNode.Parse(args)?.AsObject() ?? [];
            }
            catch (JsonException)
            {
                obj = [];
            }
            foreach (var key in obj.Select(kv => kv.Key).ToList())
            {
                // "action" picks what a tool does (open, close); it stays fixed.
                if (key != "action" && obj[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length >= 3
                    && request.Contains(s, StringComparison.OrdinalIgnoreCase) && !s.Contains('{', StringComparison.Ordinal))
                {
                    if (!names.TryGetValue(s, out var p))
                    {
                        p = key;
                        var n = 2;
                        while (names.ContainsValue(p))
                        {
                            p = key + n++;
                        }
                        names[s] = p;
                    }
                    obj[key] = "{" + p + "}";
                }
            }
            result.Add(new SkillStep(tool, obj.ToJsonString()));
        }
        return (result, names.Values.ToList());
    }

    public static string SuggestNameFor(string request) => SuggestName(request);

    internal static string SuggestName(string request)
    {
        var stop = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "please", "can", "you", "could", "the", "a", "an", "my", "for", "me", "in", "this", "that", "and", "to", "of" };
        var words = new string(request.Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !stop.Contains(w) && !w.Any(char.IsDigit)).Take(4);
        var name = SkillStore.SkillName(string.Join('_', words));
        return name.Length == 0 ? "my_skill" : name;
    }

    private static string Shape(IEnumerable<SkillStep> steps) => string.Join("|", steps.Select(s => s.Tool + s.ArgumentsJson));

    public void Dispose()
    {
        foreach (var s in _subs)
        {
            s.Dispose();
        }
    }
}
