using Microsoft.Extensions.AI;
using Starlight.Agent;
using Starlight.Diagnostics;

namespace Starlight.Tests;

public sealed class AgentLoopFixTests
{
    private static AgentLoop Loop(TestEnv env, IChatClient? client) =>
        new(() => client, env.Registry, env.Dispatcher(), env.Settings, env.KillSwitch, env.Events, env.Trace, env.Db, [], []);

    [Fact]
    public void Calls_without_results_get_a_cancelled_result_right_after_them()
    {
        var c = new Conversation();
        c.Messages.Add(new ChatMessage(ChatRole.User, "do two things"));
        c.Messages.Add(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("a", "echo"), new FunctionCallContent("b", "echo")]));
        c.Messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent("a", "done")]));
        AgentLoop.CloseDanglingCalls(c);
        Assert.Equal(4, c.Messages.Count);
        var added = Assert.IsType<FunctionResultContent>(c.Messages[3].Contents.Single());
        Assert.Equal("b", added.CallId);
        Assert.Contains("cancelled", added.Result?.ToString());
    }

    [Fact]
    public void Empty_and_repeated_call_ids_are_made_unique()
    {
        var c = new Conversation();
        c.Messages.Add(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("x", "echo")]));
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("", "echo"), new FunctionCallContent("x", "echo")]));
        AgentLoop.EnsureUniqueCallIds(response, c);
        var ids = response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Select(f => f.CallId).ToList();
        Assert.All(ids, id => Assert.False(string.IsNullOrEmpty(id)));
        Assert.DoesNotContain("x", ids);
        Assert.Equal(2, ids.Distinct().Count());
    }

    [Fact]
    public async Task Pictures_the_user_attached_are_dropped_on_the_next_turn()
    {
        using var env = new TestEnv();
        var client = new ScriptedChatClient(_ => ScriptedChatClient.Text("ok"));
        var loop = Loop(env, client);
        var c = new Conversation();
        await loop.RunAsync(c, "look at this", "chat", attachments: [new DataContent(new byte[] { 1, 2, 3 }, "image/png")]);
        Assert.Contains(client.Sent[0], m => m.Contents.OfType<DataContent>().Any());
        await loop.RunAsync(c, "thanks", "chat");
        Assert.DoesNotContain(c.Messages, m => m.Contents.OfType<DataContent>().Any());
        Assert.DoesNotContain(client.Sent[1], m => m.Contents.OfType<DataContent>().Any());
    }

    [Fact]
    public async Task Context_is_checked_again_after_a_big_tool_result()
    {
        using var env = new TestEnv();
        env.Registry.Add(new SampleTools());
        env.Settings.Update(s => s.Model.ContextSize = 5000);
        var c = new Conversation();
        for (var i = 0; i < 8; i++)
        {
            c.Messages.Add(new ChatMessage(i % 2 == 0 ? ChatRole.User : ChatRole.Assistant, new string('a', 100)));
        }
        var client = new ScriptedChatClient(
            _ => ScriptedChatClient.Call("big", []),
            _ => ScriptedChatClient.Text("done"));
        await Loop(env, client).RunAsync(c, "read it", "chat");
        Assert.DoesNotContain(client.Sent[0], m => m.Text.StartsWith(AgentLoop.SummaryPrefix, StringComparison.Ordinal));
        Assert.Contains(client.Sent[1], m => m.Text.StartsWith(AgentLoop.SummaryPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_max_steps_note_is_kept_in_the_history()
    {
        using var env = new TestEnv();
        env.Registry.Add(new SampleTools());
        env.Settings.Update(s => s.Model.MaxSteps = 2);
        var c = new Conversation();
        var client = new ScriptedChatClient(_ => ScriptedChatClient.Call("echo", new() { ["text"] = "again" }));
        await Loop(env, client).RunAsync(c, "loop", "chat");
        Assert.Equal(ChatRole.Assistant, c.Messages[^1].Role);
        Assert.Contains("stopped after 2 steps", c.Messages[^1].Text);
    }

    [Fact]
    public async Task Observers_get_a_copy_of_the_conversation()
    {
        using var env = new TestEnv();
        var seen = new TaskCompletionSource<Conversation>();
        var observer = new CapturingObserver(seen);
        var loop = new AgentLoop(() => new ScriptedChatClient(_ => ScriptedChatClient.Text("ok")), env.Registry, env.Dispatcher(), env.Settings, env.KillSwitch, env.Events, env.Trace, env.Db, [], [observer]);
        var c = new Conversation();
        await loop.RunAsync(c, "hi", "chat");
        var copy = await seen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotSame(c.Messages, copy.Messages);
        Assert.Equal(c.Messages.Count, copy.Messages.Count);
    }

    [Fact]
    public void Trace_masks_secrets_in_every_string()
    {
        using var env = new TestEnv();
        env.Trace.Write("turn.start", "t1", new { text = "my password=hunter2 and Bearer abcdefghijklmnop", nested = new { apiKey = "x" } });
        var data = env.Trace.Recent(TimeSpan.FromMinutes(1)).Single().Data!.ToString()!;
        Assert.DoesNotContain("hunter2", data);
        Assert.DoesNotContain("abcdefghijklmnop", data);
        Assert.DoesNotContain("\"x\"", data);
        var file = Directory.GetFiles(env.Paths.TraceDir).Single();
        Assert.DoesNotContain("hunter2", File.ReadAllText(file));
    }

    [Fact]
    public void Masker_hides_bearer_tokens_jwts_and_private_keys()
    {
        var text = SecretMasker.Mask("Authorization: Bearer abc.def-123456 jwt eyJhbGciOiJIUzI1.eyJzdWIiOiIxMjM0.SflKxwRJSMeKKF2Q -----BEGIN RSA PRIVATE KEY-----\nMIIE\n-----END RSA PRIVATE KEY-----");
        Assert.DoesNotContain("abc.def-123456", text);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1", text);
        Assert.DoesNotContain("MIIE", text);
    }

    private sealed class CapturingObserver(TaskCompletionSource<Conversation> seen) : ITurnObserver
    {
        public Task OnTurnCompletedAsync(Conversation conversation, string userText, string answer, CancellationToken ct)
        {
            seen.TrySetResult(conversation);
            return Task.CompletedTask;
        }
    }
}
