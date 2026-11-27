using Microsoft.Extensions.AI;
using Starlight.Eval;

namespace Starlight.Tests;

public sealed class EvalScorerTests
{
    private static ChatResponse Call(string name, Dictionary<string, object?> args) =>
        new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c", name, args)]));

    private static ChatResponse Say(string text) => new(new ChatMessage(ChatRole.Assistant, text));

    [Fact]
    public void Tool_case_checks_name_and_arguments()
    {
        var c = new EvalCase { Tool = "app", Args = new() { ["action"] = "open", ["name"] = "~notepad" } };
        Assert.True(EvalScorer.Score(c, Call("app", new() { ["action"] = "Open", ["name"] = "Notepad.exe" })).Pass);
        Assert.False(EvalScorer.Score(c, Call("app", new() { ["action"] = "focus", ["name"] = "notepad" })).Pass);
        Assert.False(EvalScorer.Score(c, Call("files_find", new() { ["name"] = "notepad" })).Pass);
        Assert.False(EvalScorer.Score(c, Say("I opened it.")).Pass);
    }

    [Fact]
    public void Alternatives_none_and_clarify()
    {
        Assert.True(EvalScorer.Score(new EvalCase { Tool = "web_search|web_research" }, Call("web_research", [])).Pass);
        Assert.True(EvalScorer.Score(new EvalCase { Expect = "none" }, Say("Paris.")).Pass);
        Assert.False(EvalScorer.Score(new EvalCase { Expect = "none" }, Call("system_info", [])).Pass);
        Assert.True(EvalScorer.Score(new EvalCase { Expect = "clarify" }, Say("Which program do you mean?")).Pass);
        Assert.False(EvalScorer.Score(new EvalCase { Expect = "clarify" }, Say("Done.")).Pass);
    }

    [Fact]
    public void Case_files_load_and_name_only_planned_tools()
    {
        var planned = new HashSet<string>
        {
            "app", "window", "screen_look", "screen_find", "screen_point", "input_click", "input_type", "input_keys",
            "memory_remember", "memory_recall", "memory_forget", "web_search", "web_read", "web_research", "web_login",
            "server_check", "server_ssh", "server_list", "code_write", "code_run", "skill_run", "system_info", "files_find",
        };
        var cases = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "cases"), "*.json").SelectMany(EvalCase.Load).ToList();
        Assert.True(cases.Count >= 40);
        Assert.Equal(cases.Count, cases.Select(c => c.Id).Distinct().Count());
        Assert.All(cases.Where(c => c.Expect == "tool"), c => Assert.All(c.Tools, t => Assert.Contains(t, planned)));
        Assert.All(cases, c => Assert.Contains(c.Expect, new[] { "tool", "none", "clarify" }));
    }
}

public sealed class EvalGateTests
{
    private static List<(string, bool)> Cases(string group, int pass, int fail) =>
        [.. Enumerable.Repeat((group, true), pass), .. Enumerable.Repeat((group, false), fail)];

    [Fact]
    public void The_gate_needs_90_overall_and_80_per_tool()
    {
        Assert.Null(Starlight.Eval.EvalGate.Failure([.. Cases("app", 19, 1), .. Cases("web_read", 9, 1)]));
        Assert.Contains("overall", Starlight.Eval.EvalGate.Failure([.. Cases("app", 8, 2)]));
        Assert.Contains("web_read", Starlight.Eval.EvalGate.Failure([.. Cases("app", 40, 0), .. Cases("web_read", 3, 2)]));
        Assert.NotNull(Starlight.Eval.EvalGate.Failure([]));
    }
}
