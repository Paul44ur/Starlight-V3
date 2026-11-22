using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Starlight.Agent;
using Starlight.Events;

namespace Starlight.Tests;

/// <summary>A model that plays back scripted replies, one per call, and records what it was sent.</summary>
internal sealed class ScriptedChatClient(params Func<IList<ChatMessage>, ChatResponseUpdate[]>[] steps) : IChatClient
{
    private int _call;
    public List<List<ChatMessage>> Sent { get; } = [];
    public List<ChatOptions?> Options { get; } = [];

    public static ChatResponseUpdate[] Text(params string[] parts) =>
        parts.Select(p => new ChatResponseUpdate(ChatRole.Assistant, p)).ToArray();

    public static ChatResponseUpdate[] Call(string name, Dictionary<string, object?> args) =>
        [new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("call" + Guid.NewGuid().ToString("N")[..6], name, args)])];

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "- summary")));

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        Sent.Add(list);
        Options.Add(options);
        var step = steps[Math.Min(_call++, steps.Length - 1)];
        foreach (var u in step(list))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return u;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

public sealed class AgentLoopTests
{
    private static AgentLoop Loop(TestEnv env, IChatClient? client) =>
        new(() => client, env.Registry, env.Dispatcher(), env.Settings, env.KillSwitch, env.Events, env.Trace, env.Db, [], []);

    [Fact]
    public async Task Plain_answer_is_streamed_and_saved()
    {
        using var env = new TestEnv();
        var client = new ScriptedChatClient(_ => ScriptedChatClient.Text("Hello", " there."));
        var deltas = new List<string>();
        using var sub = env.Events.Subscribe<AnswerDelta>(d => deltas.Add(d.Delta));
        var conversation = new Conversation();
        var outcome = await Loop(env, client).RunAsync(conversation, "hi", "chat");
        Assert.False(outcome.Failed);
        Assert.Equal("Hello there.", outcome.Answer);
        Assert.Equal(["Hello", " there."], deltas);
        Assert.Equal(ChatRole.System, client.Sent[0][0].Role);
        Assert.Equal(2, env.Db.LoadMessages(conversation.Id)!.Count);
    }

    [Fact]
    public async Task Tool_call_runs_through_dispatcher_and_result_goes_back_to_model()
    {
        using var env = new TestEnv();
        env.Registry.Add(new SampleTools());
        var client = new ScriptedChatClient(
            _ => ScriptedChatClient.Call("echo", new() { ["text"] = "pong" }),
            sent => ScriptedChatClient.Text("Tool said " + sent.Last().Contents.OfType<FunctionResultContent>().Single().Result));
        var outcome = await Loop(env, client).RunAsync(new Conversation(), "ping", "voice");
        Assert.Equal("Tool said pong", outcome.Answer);
        Assert.Single(env.Db.RecentAudit());
        Assert.NotNull(client.Options[0]?.Tools);
        Assert.All(client.Options[0]!.Tools!, t => Assert.IsNotType<AIFunction>(t, exactMatch: false));
    }

    [Fact]
    public async Task Pictures_from_tools_reach_the_model_once_and_are_not_kept()
    {
        using var env = new TestEnv();
        env.Registry.Add(new SampleTools());
        var client = new ScriptedChatClient(
            _ => ScriptedChatClient.Call("look", []),
            _ => ScriptedChatClient.Text("I see a window."));
        var conversation = new Conversation();
        await Loop(env, client).RunAsync(conversation, "what's on screen", "voice");
        Assert.Contains(client.Sent[1], m => m.Contents.OfType<DataContent>().Any());
        Assert.DoesNotContain(conversation.Messages, m => m.Contents.OfType<DataContent>().Any());
    }

    [Fact]
    public async Task Stops_after_max_steps()
    {
        using var env = new TestEnv();
        env.Registry.Add(new SampleTools());
        env.Settings.Update(s => s.Model.MaxSteps = 3);
        var client = new ScriptedChatClient(_ => ScriptedChatClient.Call("echo", new() { ["text"] = "again" }));
        var outcome = await Loop(env, client).RunAsync(new Conversation(), "loop", "voice");
        Assert.Equal(3, client.Sent.Count);
        Assert.Contains("stopped after 3 steps", outcome.Answer);
    }

    [Fact]
    public async Task No_model_and_kill_switch_give_clear_answers()
    {
        using var env = new TestEnv();
        var none = await Loop(env, null).RunAsync(new Conversation(), "hi", "voice");
        Assert.True(none.Failed);
        Assert.Contains("model isn't running", none.Answer);

        env.KillSwitch.Engage("test");
        var client = new ScriptedChatClient(_ => ScriptedChatClient.Text("x"));
        var killed = await Loop(env, client).RunAsync(new Conversation(), "hi", "voice");
        Assert.True(killed.Failed);
        Assert.Empty(client.Sent);
    }

    [Fact]
    public async Task Game_mode_pause_answers_without_calling_the_model()
    {
        using var env = new TestEnv();
        var client = new ScriptedChatClient(_ => ScriptedChatClient.Text("x"));
        var loop = Loop(env, client);
        loop.PausedReason = "Paused for a game.";
        var paused = await loop.RunAsync(new Conversation(), "hi", "voice");
        Assert.True(paused.Failed);
        Assert.Equal("Paused for a game.", paused.Answer);
        Assert.Empty(client.Sent);

        loop.PausedReason = null;
        Assert.False((await loop.RunAsync(new Conversation(), "hi", "voice")).Failed);
    }

    [Fact]
    public async Task Model_connection_error_is_reported_not_thrown()
    {
        using var env = new TestEnv();
        var client = new ScriptedChatClient(_ => throw new HttpRequestException("refused"));
        var outcome = await Loop(env, client).RunAsync(new Conversation(), "hi", "voice");
        Assert.True(outcome.Failed);
        Assert.Contains("couldn't reach my model", outcome.Answer);
    }
}
