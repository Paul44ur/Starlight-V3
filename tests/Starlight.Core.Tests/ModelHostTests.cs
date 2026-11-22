using System.Net;
using System.Net.Sockets;
using Starlight.Events;
using Starlight.Model;
using Starlight.Settings;

namespace Starlight.Tests;

public sealed class ServerArgumentsTests
{
    [Fact]
    public void Builds_the_plan_flags_with_vision_and_extra_args()
    {
        var m = new ModelSettings { Port = 9000, ContextSize = 16384, ExtraServerArgs = "--threads 8 --alias \"my model\"" };
        var args = ServerArguments.Build(m, "C:/m/model.gguf", "C:/m/mmproj.gguf");
        var line = string.Join(' ', args);
        Assert.Contains("--model C:/m/model.gguf", line);
        Assert.Contains("--port 9000", line);
        Assert.Contains("--ctx-size 16384", line);
        Assert.Contains("--jinja", line);
        Assert.Contains("--host 127.0.0.1", line);
        Assert.Contains("--mmproj C:/m/mmproj.gguf", line);
        Assert.Equal(["--threads", "8", "--alias", "my model"], args.TakeLast(4));
    }

    [Fact]
    public void No_projector_means_no_mmproj_flag()
    {
        Assert.DoesNotContain("--mmproj", ServerArguments.Build(new ModelSettings(), "m.gguf", null));
    }
}

public sealed class RestartBudgetTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void Allows_three_restarts_in_five_minutes_then_refills()
    {
        var clock = new Clock();
        var budget = new RestartBudget(3, TimeSpan.FromMinutes(5), clock);
        Assert.True(budget.TryTake());
        Assert.True(budget.TryTake());
        Assert.True(budget.TryTake());
        Assert.False(budget.TryTake());
        clock.Now += TimeSpan.FromMinutes(6);
        Assert.True(budget.TryTake());
    }
}

/// <summary>Runs ModelHost against a small Python stand-in for llama-server (Linux and the PC both have no real model in tests).</summary>
public sealed class ModelHostTests
{
    private static readonly string FakeServer = Path.Combine(AppContext.BaseDirectory, "Fakes", "fake-llama-server.py");

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    private static (TestEnv env, ModelHost host, string argsFile) Make(string mode)
    {
        var env = new TestEnv();
        var modelFile = Path.Combine(env.Root, "model.gguf");
        File.WriteAllText(modelFile, "fake");
        var port = FreePort();
        env.Settings.Update(s =>
        {
            s.Model.ModelsDir = env.Root;
            s.Model.ChatModelFile = "model.gguf";
            s.Model.Port = port;
        });
        var argsFile = Path.Combine(env.Root, "args.txt");
        Environment.SetEnvironmentVariable("FAKE_MODE", mode);
        Environment.SetEnvironmentVariable("FAKE_ARGS_FILE", argsFile);
        var host = new ModelHost(env.Settings, env.Events, env.Paths, FakeServer) { StartTimeout = TimeSpan.FromSeconds(20) };
        return (env, host, argsFile);
    }

    private static bool CanRun => !OperatingSystem.IsWindows() && File.Exists("/usr/bin/python3");

    [Fact]
    public async Task Starts_reports_ready_and_gives_a_client()
    {
        Assert.SkipUnless(CanRun, "needs python3 on Linux");
        var (env, host, argsFile) = Make("slow");
        using var _ = env;
        await using var __ = host;
        var states = new List<string>();
        using var sub = env.Events.Subscribe<ModelStatus>(s => states.Add(s.Status));
        await host.StartAsync();
        Assert.Equal(ModelState.Ready, host.State);
        Assert.NotNull(host.ChatClient);
        Assert.Equal(["Starting", "Ready"], states);
        Assert.Contains("--jinja", File.ReadAllText(argsFile));
        await host.StopAsync();
        Assert.Null(host.ChatClient);
    }

    [Fact]
    public async Task Server_that_dies_while_loading_fails_with_its_output_and_no_restart()
    {
        Assert.SkipUnless(CanRun, "needs python3 on Linux");
        var (env, host, argsFile) = Make("die");
        using var _ = env;
        await using var __ = host;
        var ex = await Assert.ThrowsAsync<ModelHostException>(() => host.StartAsync());
        Assert.Contains("cannot load model", ex.Message);
        Assert.Equal(ModelState.Failed, host.State);
        await Task.Delay(1000);
        Assert.Single(File.ReadAllLines(argsFile));
    }

    [Fact]
    public async Task Crash_after_start_is_restarted()
    {
        Assert.SkipUnless(CanRun, "needs python3 on Linux");
        var (env, host, argsFile) = Make("ok");
        using var _ = env;
        await using var __ = host;
        await host.StartAsync();
        using var http = new HttpClient();
        try { await http.GetAsync(new Uri(host.BaseUri, "exit")); } catch (HttpRequestException) { }
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && (File.ReadAllLines(argsFile).Length < 2 || host.State != ModelState.Ready))
        {
            await Task.Delay(100);
        }
        Assert.Equal(2, File.ReadAllLines(argsFile).Length);
        Assert.Equal(ModelState.Ready, host.State);
    }

    [Fact]
    public async Task Missing_model_gives_a_readable_error()
    {
        var (env, host, _) = Make("ok");
        using var e = env;
        await using var h = host;
        env.Settings.Update(s => s.Model.ChatModelFile = "nope.gguf");
        var ex = await Assert.ThrowsAsync<ModelHostException>(() => host.StartAsync());
        Assert.Contains("model file is missing", ex.Message);
    }
}
