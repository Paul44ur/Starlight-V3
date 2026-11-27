using System.ComponentModel;
using Starlight.Tools;
using Starlight.Tools.Desktop;

namespace Starlight.Tests;

public sealed class DispatcherFixTests
{
    private static Dictionary<string, object?> Args(params (string, object?)[] a) => a.ToDictionary(x => x.Item1, x => x.Item2);

    [Fact]
    public async Task Whitespace_around_an_action_still_asks()
    {
        using var env = new TestEnv();
        env.Registry.Add(new SampleTools());
        var approvals = new FixedApprovals(ApprovalAnswer.No);
        var r = await env.Dispatcher(approvals).InvokeAsync("thing", Args(("action", " Close ")), "t1", "voice", default);
        Assert.False(r.Ok);
        Assert.Equal(1, approvals.Asked);
    }

    [Fact]
    public async Task Always_for_one_action_is_not_stored_for_the_whole_tool()
    {
        using var env = new TestEnv();
        env.Registry.Add(new SampleTools());
        var approvals = new FixedApprovals(ApprovalAnswer.Always);
        var d = env.Dispatcher(approvals);
        await d.InvokeAsync("thing", Args(("action", "close")), "t1", "voice", default);
        Assert.False(approvals.Last!.CanRemember);
        Assert.False(env.Settings.Current.PermissionOverrides.ContainsKey("thing"));
        await d.InvokeAsync("thing", Args(("action", "close")), "t2", "voice", default);
        Assert.Equal(2, approvals.Asked);
    }

    [Fact]
    public async Task A_whole_tool_ask_can_be_remembered()
    {
        using var env = new TestEnv();
        env.Registry.Add(new SampleTools());
        var approvals = new FixedApprovals(ApprovalAnswer.No);
        await env.Dispatcher(approvals).InvokeAsync("delete_thing", Args(("name", "a")), "t1", "voice", default);
        Assert.True(approvals.Last!.CanRemember);
    }

    [Fact]
    public async Task A_hint_that_throws_asks_instead_of_running()
    {
        using var env = new TestEnv();
        var tools = new ThrowingHintTools();
        env.Registry.Add(tools);
        var approvals = new FixedApprovals(ApprovalAnswer.No);
        var r = await env.Dispatcher(approvals).InvokeAsync("hinted", Args(), "t1", "voice", default);
        Assert.False(r.Ok);
        Assert.Equal(1, approvals.Asked);
        Assert.Equal(0, tools.Runs);
    }

    [Fact]
    public async Task A_cancelled_question_counts_as_no()
    {
        using var env = new TestEnv();
        var tools = new SampleTools();
        env.Registry.Add(tools);
        var r = await env.Dispatcher(new CancellingApprovals()).InvokeAsync("delete_thing", Args(("name", "a")), "t1", "voice", default);
        Assert.False(r.Ok);
        Assert.Equal(0, tools.Deletes);
    }

    [Fact]
    public async Task The_question_shows_long_values_and_masks_secrets()
    {
        using var env = new TestEnv();
        env.Registry.Add(new SampleTools());
        var approvals = new FixedApprovals(ApprovalAnswer.No);
        var name = new string('a', 300) + " password=hunter2";
        await env.Dispatcher(approvals).InvokeAsync("delete_thing", Args(("name", name)), "t1", "voice", default);
        Assert.Contains(new string('a', 300), approvals.Last!.Question);
        Assert.DoesNotContain("hunter2", approvals.Last.Question);
    }

    private sealed class ThrowingHintTools : IToolProvider, IPermissionHint
    {
        public int Runs { get; private set; }

        [Tool("hinted"), Description("Runs.")]
        public string Hinted() { Runs++; return "ran"; }

        public Permission? PermissionFor(string tool, IDictionary<string, object?> arguments) => throw new InvalidOperationException("bug");
    }

    private sealed class CancellingApprovals : IApprovalService
    {
        public Task<ApprovalAnswer> RequestAsync(ApprovalRequest request, CancellationToken ct) => throw new OperationCanceledException();
    }
}

public sealed class AppMatcherFixTests
{
    private static readonly AppEntry[] Apps =
    [
        new("Visual Studio Code", "code.lnk", AppKind.Shortcut),
        new("Visual Studio Installer", "vsi.lnk", AppKind.Shortcut),
        new("Spotify", "spotify", AppKind.StoreApp),
    ];

    [Fact]
    public void A_loose_match_with_several_apps_is_not_guessed()
    {
        var (pick, candidates) = AppMatcher.Choose("studio visual", Apps);
        Assert.Null(pick);
        Assert.Equal(2, candidates.Count);
    }

    [Fact]
    public void A_clear_match_is_picked()
    {
        Assert.Equal("Spotify", AppMatcher.Choose("spotify", Apps).Pick?.Name);
        Assert.Equal("Visual Studio Code", AppMatcher.Choose("visual studio code", Apps).Pick?.Name);
    }

    [Theory]
    [InlineData(@"C:\Users\x\Downloads\setup.exe", true)]
    [InlineData(@"C:\x\run.BAT", true)]
    [InlineData(@"C:\x\invoice.pdf.lnk", true)]
    [InlineData(@"C:\x\script.ps1", true)]
    [InlineData(@"C:\x\noextension", true)]
    [InlineData(@"C:\x\photo.jpg", false)]
    [InlineData(@"C:\x\notes.txt", false)]
    public void Runnable_files_are_recognised(string path, bool runnable) =>
        Assert.Equal(runnable, AppMatcher.IsRunnable(path));

    private static readonly List<(string Title, string Process)> Windows =
    [
        ("Untitled - Notepad", "notepad"),
        ("notes.txt - Notepad", "notepad"),
        ("Inbox - Outlook", "outlook"),
    ];

    [Fact]
    public void Closing_refuses_an_ambiguous_window()
    {
        Assert.Equal(-1, AppMatcher.PickWindow("notepad", Windows, unique: true, out var problem));
        Assert.Contains("Several windows", problem);
        Assert.Equal(0, AppMatcher.PickWindow("notepad", Windows, unique: false, out _));
    }

    [Fact]
    public void Symbols_only_never_match_a_window()
    {
        Assert.Equal(-1, AppMatcher.PickWindow("!!!", Windows, unique: false, out var problem));
        Assert.NotNull(problem);
    }

    [Fact]
    public void An_exact_title_wins_over_a_loose_match()
    {
        Assert.Equal(1, AppMatcher.PickWindow("notes.txt - Notepad", Windows, unique: true, out _));
    }
}
