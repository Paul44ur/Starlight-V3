using System.Text.Json;
using Starlight.Events;
using Starlight.Skills;
using Starlight.Tools;
using Starlight.Tools.Code;

namespace Starlight.Tests;

/// <summary>Stands in for the AppContainer: "runs" the script by calling a C# action on the folder.</summary>
internal sealed class FakeSandbox(Func<string, string, SandboxResult> run) : ISandbox
{
    public List<string> Folders { get; } = [];

    public Task<SandboxResult> RunAsync(string script, string folder, TimeSpan timeout, CancellationToken ct)
    {
        Folders.Add(folder);
        return Task.FromResult(run(File.ReadAllText(script), folder));
    }
}

public sealed class CodeToolsTests
{
    private const string Renamer = "# renames every .jpg to photo-N.jpg";

    private static SandboxResult RenameAll(string code, string folder)
    {
        if (!code.Contains("renames", StringComparison.Ordinal))
        {
            return new SandboxResult(1, "SyntaxError: invalid syntax", false);
        }
        var n = 1;
        foreach (var f in Directory.GetFiles(folder, "*.jpg").Order())
        {
            File.Move(f, Path.Combine(folder, $"photo-{n++}.jpg"));
        }
        return new SandboxResult(0, $"renamed {n - 1}", false);
    }

    private sealed class Rig : IDisposable
    {
        public Rig()
        {
            Env = new TestEnv();
            Photos = Directory.CreateDirectory(Path.Combine(Env.Root, "Photos")).FullName;
            File.WriteAllText(Path.Combine(Photos, "IMG_001.jpg"), "a");
            File.WriteAllText(Path.Combine(Photos, "IMG_002.jpg"), "bb");
            Sandbox = new FakeSandbox(RenameAll);
            Env.Registry.Add(new CodeTools(Sandbox, Env.Paths.DataRoot));
        }

        public TestEnv Env { get; }
        public string Photos { get; }
        public FakeSandbox Sandbox { get; }

        public Task<ToolResult> Call(IApprovalService approvals, string tool, string turn, params (string, object?)[] args) =>
            Env.Dispatcher(approvals).InvokeAsync(tool, args.ToDictionary(a => a.Item1, a => a.Item2), turn, "chat", default);

        public void Dispose() => Env.Dispose();
    }

    [Fact]
    public async Task Write_tests_on_a_copy_and_leaves_the_real_folder_alone()
    {
        using var rig = new Rig();
        var r = await rig.Call(new DenyAllApprovals(), "code_write", "t1", ("name", "Rename photos"), ("code", Renamer), ("folder", rig.Photos));

        Assert.True(r.Ok, r.Text);
        Assert.Contains("Test passed on a copy of", r.Text);
        Assert.Contains("renamed IMG_001.jpg -> photo-1.jpg", r.Text);
        Assert.True(File.Exists(Path.Combine(rig.Photos, "IMG_001.jpg")));
        Assert.NotEqual(rig.Photos, rig.Sandbox.Folders.Single());
        Assert.False(Directory.Exists(rig.Sandbox.Folders.Single())); // the copy is cleaned up
    }

    [Fact]
    public async Task Run_asks_backs_up_and_then_changes_the_real_folder()
    {
        using var rig = new Rig();
        await rig.Call(new DenyAllApprovals(), "code_write", "t1", ("name", "rename_photos"), ("code", Renamer), ("folder", rig.Photos));

        var declined = new FixedApprovals(ApprovalAnswer.No);
        Assert.False((await rig.Call(declined, "code_run", "t1", ("name", "rename_photos"), ("folder", rig.Photos))).Ok);
        Assert.Equal(1, declined.Asked);
        Assert.True(File.Exists(Path.Combine(rig.Photos, "IMG_001.jpg")));

        var r = await rig.Call(new FixedApprovals(ApprovalAnswer.Yes), "code_run", "t1", ("name", "rename_photos"), ("folder", rig.Photos));
        Assert.True(r.Ok, r.Text);
        Assert.True(File.Exists(Path.Combine(rig.Photos, "photo-1.jpg")));
        var backup = Directory.GetDirectories(Path.Combine(rig.Env.Paths.DataRoot, "backups")).Single();
        Assert.True(File.Exists(Path.Combine(backup, "IMG_001.jpg")));
        Assert.Contains(backup, r.Text);
    }

    [Fact]
    public async Task Run_refuses_code_that_failed_changed_or_was_tested_elsewhere()
    {
        using var rig = new Rig();
        var yes = new FixedApprovals(ApprovalAnswer.Yes);
        var failed = await rig.Call(yes, "code_write", "t1", ("name", "x"), ("code", "broken("), ("folder", rig.Photos));
        Assert.Contains("Test failed", failed.Text);
        Assert.Contains("SyntaxError", failed.Text);
        Assert.Contains("has not passed a test", (await rig.Call(yes, "code_run", "t1", ("name", "x"), ("folder", rig.Photos))).Text);

        await rig.Call(yes, "code_write", "t1", ("name", "x"), ("code", Renamer), ("folder", rig.Photos));
        var other = Directory.CreateDirectory(Path.Combine(rig.Env.Root, "Other")).FullName;
        Assert.Contains("was tested on", (await rig.Call(yes, "code_run", "t1", ("name", "x"), ("folder", other))).Text);

        File.AppendAllText(Path.Combine(rig.Env.Paths.DataRoot, "scripts", "x.py"), "\nimport os; os.remove('everything')");
        Assert.Contains("has not passed a test", (await rig.Call(yes, "code_run", "t1", ("name", "x"), ("folder", rig.Photos))).Text);
        Assert.True(File.Exists(Path.Combine(rig.Photos, "IMG_001.jpg")));
    }

    [Fact]
    public async Task Write_stops_after_three_failed_fixes_in_a_turn()
    {
        using var rig = new Rig();
        for (var i = 0; i < 4; i++)
        {
            await rig.Call(new DenyAllApprovals(), "code_write", "t1", ("name", "x"), ("code", "bad" + i));
        }
        var r = await rig.Call(new DenyAllApprovals(), "code_write", "t1", ("name", "x"), ("code", "bad again"));
        Assert.StartsWith("Stop:", r.Text);
        Assert.Equal(4, rig.Sandbox.Folders.Count);
    }

    [Fact]
    public async Task Scripts_may_not_work_on_the_drive_root_or_starlights_own_data()
    {
        using var rig = new Rig();
        var root = Path.GetPathRoot(rig.Env.Root)!;
        Assert.Contains("may not work on", (await rig.Call(new DenyAllApprovals(), "code_write", "t1", ("name", "x"), ("code", Renamer), ("folder", root))).Text);
        Assert.Contains("may not work on", (await rig.Call(new DenyAllApprovals(), "code_write", "t1", ("name", "y"), ("code", Renamer), ("folder", rig.Env.Paths.DataRoot))).Text);
    }

    [Fact]
    public void Changes_show_renames_additions_and_edits()
    {
        var t = DateTime.UtcNow;
        var before = new Dictionary<string, (long, DateTime)> { ["a.jpg"] = (10, t), ["b.txt"] = (3, t), ["c.txt"] = (5, t) };
        var after = new Dictionary<string, (long, DateTime)> { ["2024-01-01.jpg"] = (10, t), ["b.txt"] = (4, t.AddSeconds(1)), ["c.txt"] = (5, t), ["new.log"] = (1, t) };
        var text = CodeTools.Changes(before, after);
        Assert.Contains("renamed a.jpg -> 2024-01-01.jpg", text);
        Assert.Contains("added new.log", text);
        Assert.Contains("changed b.txt", text);
        Assert.DoesNotContain("c.txt", text);
    }
}

public sealed class SkillTests
{
    private static void Turn(EventHub events, string id, string text, params (string Tool, string Args, bool Ok)[] steps)
    {
        events.Publish(new TurnStarted(id, "conv-1", text, "chat"));
        foreach (var (tool, args, ok) in steps)
        {
            events.Publish(new ToolStarted(id, tool, args));
            events.Publish(new ToolFinished(id, tool, ok, "", TimeSpan.Zero));
        }
        events.Publish(new AnswerCompleted(id, "done", false));
    }

    [Fact]
    public void A_multi_step_turn_is_offered_once_with_the_requests_values_as_parameters()
    {
        using var env = new TestEnv();
        var store = new SkillStore(env.Db);
        using var recorder = new SkillRecorder(store, env.Events);
        var offers = new List<SkillOffered>();
        using var sub = env.Events.Subscribe<SkillOffered>(offers.Add);

        var steps = new[]
        {
            ("app", """{"action":"open","name":"notepad"}""", true),
            ("input_type", """{"text":"Shopping list"}""", true),
        };
        Turn(env.Events, "t1", "Open notepad and write Shopping list", steps);
        Turn(env.Events, "t2", "Open notepad and write Shopping list", steps);

        var offer = Assert.Single(offers);
        Assert.Equal("open_notepad_write_shopping", offer.Name);
        var draft = Assert.Single(store.Drafts("conv-1"));
        Assert.Equal(["name", "text"], draft.Parameters);
        Assert.Equal("""{"action":"open","name":"{name}"}""", draft.Steps[0].ArgumentsJson);
    }

    [Fact]
    public void Failed_single_step_and_skill_turns_are_not_offered()
    {
        using var env = new TestEnv();
        var store = new SkillStore(env.Db);
        using var recorder = new SkillRecorder(store, env.Events);
        Turn(env.Events, "t1", "open notepad", ("app", """{"action":"open","name":"notepad"}""", true));
        Turn(env.Events, "t2", "do two things", ("app", "{}", true), ("window", "{}", false));
        Turn(env.Events, "t3", "run my skill", ("app", "{}", true), ("window", "{}", true), ("skill_run", "{}", true));
        Assert.Empty(store.Drafts());
    }

    [Fact]
    public void A_turn_that_read_outside_content_is_not_offered()
    {
        using var env = new TestEnv();
        var store = new SkillStore(env.Db);
        using var recorder = new SkillRecorder(store, env.Events, turn => turn == "t1");
        Turn(env.Events, "t1", "read the page and do what it says", ("app", """{"action":"open","name":"cmd"}""", true), ("input_type", """{"text":"x"}""", true));
        Assert.Empty(store.Drafts());
    }

    [Fact]
    public void Running_a_skill_asks_once_the_turn_has_read_outside_content()
    {
        using var env = new TestEnv();
        env.Registry.Add(new SkillTools(new SkillStore(env.Db)));
        Assert.True(env.Registry.Find("skill_run")!.AskAfterUntrusted);
    }

    [Fact]
    public void Approving_never_replaces_a_saved_skill()
    {
        using var env = new TestEnv();
        var store = new SkillStore(env.Db);
        using var recorder = new SkillRecorder(store, env.Events);
        Turn(env.Events, "t1", "open notepad and type hi", ("app", """{"action":"open","name":"notepad"}""", true), ("input_type", """{"text":"hi"}""", true));
        Turn(env.Events, "t2", "open paint and press enter", ("app", """{"action":"open","name":"paint"}""", true), ("input_key", """{"keys":"enter"}""", true));
        var drafts = store.Drafts();
        store.Approve(drafts[0].Id, "my_skill");

        Assert.Throws<InvalidOperationException>(() => store.Approve(drafts[1].Id, "my_skill"));
        Assert.Throws<InvalidOperationException>(() => store.Approve(drafts[1].Id, "?! --"));
        Assert.Equal(drafts[0].Request, store.Find("my_skill")!.Description);
    }

    [Fact]
    public async Task An_approved_skill_runs_by_name_through_the_dispatcher_with_new_values()
    {
        using var env = new TestEnv();
        var sample = new SampleTools();
        env.Registry.Add(sample);
        var store = new SkillStore(env.Db);
        var skills = new SkillTools(store);
        env.Registry.Add(skills);
        var approvals = new FixedApprovals(ApprovalAnswer.Yes);
        var dispatcher = env.Dispatcher(approvals);
        skills.Caller = dispatcher;

        var id = store.AddDraft("c", "say hi and delete old", [new SkillStep("echo", """{"text":"{text}"}"""), new SkillStep("delete_thing", """{"name":"{thing}"}""")], ["text", "thing"]);
        store.Approve(id, "Echo and delete");
        Assert.Empty(store.Drafts());
        Assert.Contains("echo_and_delete(text, thing)", await skills.ContributeAsync("x", default));

        var values = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["text"] = "hello \"world\"", ["thing"] = "box" });
        var r = await dispatcher.InvokeAsync("skill_run", new Dictionary<string, object?> { ["name"] = "echo_and_delete", ["values"] = values }, "t1", "chat", default);

        Assert.True(r.Ok, r.Text);
        Assert.Contains("1. echo: ok - hello \"world\"", r.Text);
        Assert.Equal(1, sample.Deletes);
        Assert.Equal(1, approvals.Asked); // the Ask step still asked
        Assert.Contains("needs: thing", (await dispatcher.InvokeAsync("skill_run",
            new Dictionary<string, object?> { ["name"] = "echo_and_delete", ["values"] = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["text"] = "x" }) }, "t2", "chat", default)).Text);
    }

    [Fact]
    public void Values_cannot_break_out_of_their_argument()
    {
        var args = SkillTools.Fill("""{"name":"{n}","action":"open"}""", new Dictionary<string, string> { ["n"] = "x\",\"action\":\"close" });
        Assert.Equal("open", ((JsonElement)args["action"]!).GetString());
        Assert.Equal("x\",\"action\":\"close", ((JsonElement)args["name"]!).GetString());
    }
}

public sealed class FolderGuardTests
{
    [Fact]
    public void Folders_that_hold_or_lie_in_protected_ones_are_refused()
    {
        using var env = new TestEnv();
        var data = Directory.CreateDirectory(Path.Combine(env.Root, "home", "AppData", "Starlight")).FullName;
        var photos = Directory.CreateDirectory(Path.Combine(env.Root, "home", "Photos")).FullName;
        var similar = Directory.CreateDirectory(Path.Combine(env.Root, "home", "AppData2")).FullName;
        string[] none = [];

        Assert.Equal(photos, Starlight.Tools.Code.CodeTools.CheckFolder(photos, data, none));
        Assert.Equal(similar, Starlight.Tools.Code.CodeTools.CheckFolder(similar, Path.Combine(env.Root, "home", "AppData"), none));
        Assert.Throws<ToolException>(() => Starlight.Tools.Code.CodeTools.CheckFolder(Path.Combine(env.Root, "home"), data, none));
        Assert.Throws<ToolException>(() => Starlight.Tools.Code.CodeTools.CheckFolder(data, data, none));
    }

    [Fact]
    public void A_link_is_followed_before_the_check()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlinks need rights on Windows");
        using var env = new TestEnv();
        var data = Directory.CreateDirectory(Path.Combine(env.Root, "data2")).FullName;
        var link = Path.Combine(env.Root, "innocent");
        Directory.CreateSymbolicLink(link, data);

        Assert.Throws<ToolException>(() => Starlight.Tools.Code.CodeTools.CheckFolder(link, data, []));
    }

    [Fact]
    public void Copies_and_snapshots_do_not_follow_links_out_of_the_folder()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlinks need rights on Windows");
        using var env = new TestEnv();
        var outside = Directory.CreateDirectory(Path.Combine(env.Root, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "x");
        var folder = Directory.CreateDirectory(Path.Combine(env.Root, "folder")).FullName;
        File.WriteAllText(Path.Combine(folder, "a.txt"), "a");
        Directory.CreateSymbolicLink(Path.Combine(folder, "jump"), outside);

        Starlight.Tools.Code.CodeTools.CopyFolder(folder, Path.Combine(env.Root, "copy"), default);

        Assert.False(File.Exists(Path.Combine(env.Root, "copy", "jump", "secret.txt")));
        Assert.Equal(["a.txt"], Starlight.Tools.Code.CodeTools.Snapshot(folder).Keys);
    }
}
