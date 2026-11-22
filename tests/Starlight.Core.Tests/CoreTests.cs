using System.IO.Compression;
using Microsoft.Extensions.AI;
using Starlight.Diagnostics;
using Starlight.Events;
using Starlight.Settings;

namespace Starlight.Tests;

public sealed class SettingsTests
{
    [Fact]
    public void Missing_file_gives_defaults()
    {
        using var env = new TestEnv();
        Assert.Equal("Hey Starlight", env.Settings.Current.Voice.WakePhrase);
        Assert.Equal("Ctrl+Alt+J", env.Settings.Current.General.KillHotkey);
    }

    [Fact]
    public void Update_saves_and_reloads()
    {
        using var env = new TestEnv();
        env.Settings.Update(s => s.Voice.WakePhrase = "Hey Nova");
        env.Settings.Update(s => s.General.AssistantName = "Nova");
        var again = new SettingsStore(env.Paths.SettingsFile);
        Assert.Equal("Hey Nova", again.Current.Voice.WakePhrase);
        Assert.Equal("Nova", again.Current.General.AssistantName);
        Assert.True(File.Exists(env.Paths.SettingsFile + ".bak"));
    }

    [Fact]
    public void Broken_file_is_kept_aside_and_defaults_load()
    {
        using var env = new TestEnv();
        File.WriteAllText(env.Paths.SettingsFile, "{ not json");
        var store = new SettingsStore(env.Paths.SettingsFile);
        Assert.Equal("Starlight", store.Current.General.AssistantName);
        Assert.Single(Directory.GetFiles(env.Paths.DataRoot, "settings.json.broken-*"));
    }

    [Fact]
    public void Unknown_and_missing_fields_are_tolerated()
    {
        using var env = new TestEnv();
        File.WriteAllText(env.Paths.SettingsFile, """{ "voice": { "wakePhrase": "Hi there" }, "somethingOld": 1 }""");
        var store = new SettingsStore(env.Paths.SettingsFile);
        Assert.Equal("Hi there", store.Current.Voice.WakePhrase);
        Assert.Equal(8737, store.Current.Model.Port);
    }
}

public sealed class KillSwitchTests
{
    [Fact]
    public void Engage_cancels_token_runs_cleanups_and_resume_gives_a_fresh_token()
    {
        var hub = new EventHub();
        var ks = new KillSwitch(hub);
        var cleaned = new List<string>();
        ks.RegisterCleanup("bad", () => throw new InvalidOperationException("x"));
        ks.RegisterCleanup("keys", () => cleaned.Add("keys"));
        var token = ks.Token;
        ks.Engage("test");
        Assert.True(token.IsCancellationRequested);
        Assert.Equal(["keys"], cleaned);
        Assert.Throws<OperationCanceledException>(ks.ThrowIfEngaged);
        ks.Resume();
        Assert.False(ks.IsEngaged);
        Assert.False(ks.Token.IsCancellationRequested);
    }
}

public sealed class EventHubTests
{
    [Fact]
    public void Broken_listener_does_not_stop_others_and_unsubscribe_works()
    {
        var hub = new EventHub();
        var got = 0;
        using var bad = hub.Subscribe(_ => throw new InvalidOperationException());
        var good = hub.Subscribe<Notice>(_ => got++);
        hub.Publish(new Notice("a"));
        good.Dispose();
        hub.Publish(new Notice("b"));
        Assert.Equal(1, got);
    }
}

public sealed class DiagnosticsTests
{
    [Fact]
    public void Problem_report_and_export_contain_the_evidence()
    {
        using var env = new TestEnv();
        File.WriteAllText(Path.Combine(env.Paths.LogsDir, "errors-20261122.log"), "{\"@l\":\"Error\"}");
        File.WriteAllText(Path.Combine(env.Paths.CrashDir, "crash-1.txt"), "boom");
        env.Trace.Write("turn.start", "t1", new { text = "open notepad" });
        var report = new ProblemReports(env.Paths, env.Trace).Save("The orb froze");
        Assert.Contains("The orb froze", File.ReadAllText(report));
        Assert.Contains("open notepad", File.ReadAllText(report));

        var zip = new DiagnosticsExporter(env.Paths, env.Settings.ToJson, () => "test system").Export(env.Root);
        using var archive = ZipFile.OpenRead(zip);
        var names = archive.Entries.Select(e => e.FullName.Replace('\\', '/')).ToList();
        Assert.Contains(names, n => n.EndsWith("errors-20261122.log", StringComparison.Ordinal));
        Assert.Contains(names, n => n.EndsWith("crash-1.txt", StringComparison.Ordinal));
        Assert.Contains(names, n => n.Contains("trace-", StringComparison.Ordinal));
        Assert.Contains(names, n => n.EndsWith("settings.json", StringComparison.Ordinal));
        Assert.Contains(names, n => n.EndsWith("system.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void Secrets_are_masked_in_text()
    {
        var masked = SecretMasker.Mask("key sk-abcdefghijklmnopqrstuvwx and password=hunter2");
        Assert.DoesNotContain("abcdefghijklmnop", masked);
        Assert.DoesNotContain("hunter2", masked);
    }
}

public sealed class DatabaseTests
{
    [Fact]
    public void Conversations_round_trip_with_tool_calls()
    {
        using var env = new TestEnv();
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "open notepad"),
            new(ChatRole.Assistant, [new FunctionCallContent("c1", "open_app", new Dictionary<string, object?> { ["name"] = "notepad" })]),
            new(ChatRole.Tool, [new FunctionResultContent("c1", "Opened Notepad.")]),
            new(ChatRole.Assistant, "Notepad is open."),
        };
        env.Db.SaveConversation("c", "Notepad", messages, DateTimeOffset.Now);
        var loaded = env.Db.LoadMessages("c")!;
        Assert.Equal(4, loaded.Count);
        Assert.IsType<FunctionCallContent>(loaded[1].Contents[0]);
        Assert.Equal("Notepad is open.", loaded[3].Text);
        Assert.Single(env.Db.ListConversations("notepad"));
        env.Db.RenameConversation("c", "Editor");
        env.Db.PinConversation("c", true);
        Assert.True(env.Db.ListConversations().Single().Pinned);
        env.Db.DeleteConversation("c");
        Assert.Empty(env.Db.ListConversations());
    }
}

public sealed class ConversationsTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void Voice_continues_the_current_conversation_for_ten_minutes()
    {
        using var env = new TestEnv();
        var clock = new Clock();
        var all = new Starlight.Agent.Conversations(env.Db, clock);
        var first = all.ForVoice();
        clock.Now += TimeSpan.FromMinutes(5);
        Assert.Same(first, all.ForVoice());
        clock.Now += TimeSpan.FromMinutes(11);
        Assert.NotSame(first, all.ForVoice());
    }

    [Fact]
    public void A_saved_conversation_loads_once_and_is_shared()
    {
        using var env = new TestEnv();
        env.Db.SaveConversation("x", "Notepad", [new ChatMessage(ChatRole.User, "open notepad")], DateTimeOffset.Now);
        var all = new Starlight.Agent.Conversations(env.Db);
        var a = all.Get("x");
        Assert.Same(a, all.Get("x"));
        Assert.Equal("Notepad", a.Title);
        Assert.Single(a.Messages);
        Starlight.Agent.Conversation? seen = null;
        all.CurrentChanged += c => seen = c;
        all.Use(a);
        Assert.Same(a, seen);
        Assert.Same(a, all.ForVoice());
    }
}

public sealed class JobObjectTests
{
    [Fact]
    public void Command_line_quotes_spaces_quotes_and_trailing_backslashes()
    {
        var line = Starlight.Platform.JobObject.CommandLine(@"C:\Program Files\Edge\msedge.exe",
            ["--headless=new", @"--user-data-dir=C:\Users\a b\Profile\", "say \"hi\"", @"a\\b", @"x\""", ""]);
        Assert.Equal("\"C:\\Program Files\\Edge\\msedge.exe\" --headless=new \"--user-data-dir=C:\\Users\\a b\\Profile\\\\\" "
            + "\"say \\\"hi\\\"\" a\\\\b \"x\\\\\\\"\" \"\"", line);
    }

    [Fact]
    public void Start_runs_the_program_with_its_arguments()
    {
        using var job = new Starlight.Platform.JobObject();
        var exe = OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : "/bin/sh";
        string[] args = OperatingSystem.IsWindows() ? ["/c", "exit 7"] : ["-c", "exit 7"];
        using var p = job.Start(exe, args);
        Assert.True(p.WaitForExit(10_000));
        Assert.Equal(7, p.ExitCode);
    }
}
