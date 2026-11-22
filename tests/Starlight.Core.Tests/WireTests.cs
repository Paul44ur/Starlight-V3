using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Starlight.Agent;
using Starlight.Model;
using Starlight.Skills;

namespace Starlight.Tests;

/// <summary>
/// The real client path: the OpenAI client from ModelHost talks to a local stand-in that answers in
/// llama-server's streaming format, with a tool call split over chunks.
/// </summary>
public sealed class WireTests
{
    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    private static string Chunk(object delta, string? finish = null) =>
        "data: " + JsonSerializer.Serialize(new
        {
            id = "c1", @object = "chat.completion.chunk", created = 1, model = "local",
            choices = new[] { new { index = 0, delta, finish_reason = finish } },
        }) + "\n\n";

    [Fact]
    public async Task A_streamed_tool_call_runs_and_its_result_goes_back_with_the_same_id()
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var bodies = new List<JsonDocument>();
        var server = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                var ctx = await listener.GetContextAsync();
                using (var reader = new StreamReader(ctx.Request.InputStream))
                {
                    bodies.Add(JsonDocument.Parse(await reader.ReadToEndAsync()));
                }
                ctx.Response.ContentType = "text/event-stream";
                var sse = i == 0
                    ? Chunk(new { role = "assistant", content = (string?)null, tool_calls = new[] { new { index = 0, id = "call_abc", type = "function", function = new { name = "echo", arguments = "{\"te" } } } })
                      + Chunk(new { tool_calls = new[] { new { index = 0, function = new { arguments = "xt\":\"pong\"}" } } } })
                      + Chunk(new { }, "tool_calls") + "data: [DONE]\n\n"
                    : Chunk(new { role = "assistant", content = "Echo said " }) + Chunk(new { content = "pong." }) + Chunk(new { }, "stop") + "data: [DONE]\n\n";
                var bytes = Encoding.UTF8.GetBytes(sse);
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });

        using var env = new TestEnv();
        env.Registry.Add(new SampleTools());
        env.Registry.Add(new SkillTools(new SkillStore(env.Db)));
        var client = ModelHost.CreateClient(new Uri($"http://127.0.0.1:{port}/"));
        var loop = new AgentLoop(() => client, env.Registry, env.Dispatcher(), env.Settings, env.KillSwitch, env.Events, env.Trace, env.Db, [], []);

        var outcome = await loop.RunAsync(new Conversation(), "say pong", "chat");
        await server.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(outcome.Failed, outcome.Answer);
        Assert.Equal("Echo said pong.", outcome.Answer);
        var tools = bodies[0].RootElement.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("function")).ToList();
        Assert.Contains(tools, t => t.GetProperty("name").GetString() == "echo");
        var skill = tools.Single(t => t.GetProperty("name").GetString() == "skill_run");
        Assert.Equal("object", skill.GetProperty("parameters").GetProperty("properties").GetProperty("values").GetProperty("type").ToString().Contains("object", StringComparison.Ordinal) ? "object" : skill.GetRawText());
        var messages = bodies[1].RootElement.GetProperty("messages").EnumerateArray().ToList();
        var call = messages.Single(m => m.TryGetProperty("tool_calls", out _)).GetProperty("tool_calls")[0];
        Assert.Equal("call_abc", call.GetProperty("id").GetString());
        Assert.Equal("{\"text\":\"pong\"}", JsonSerializer.Serialize(JsonDocument.Parse(call.GetProperty("function").GetProperty("arguments").GetString()!).RootElement));
        var result = messages.Single(m => m.GetProperty("role").GetString() == "tool");
        Assert.Equal("call_abc", result.GetProperty("tool_call_id").GetString());
        Assert.Contains("pong", result.GetProperty("content").ToString());
    }
}
