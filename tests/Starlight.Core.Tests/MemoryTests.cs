using Starlight.Events;
using Starlight.Memory;
using Starlight.Tools;

namespace Starlight.Tests;

public sealed class MemoryTests
{
    [Fact]
    public void A_fact_on_the_same_topic_replaces_the_old_one_and_keeps_it_in_history()
    {
        using var env = new TestEnv();
        var store = new MemoryStore(env.Db);

        var (_, first) = store.Remember("The user's sister Anna has her birthday on 3 March.", "Sister birthday", "c1");
        var (saved, replaced) = store.Remember("The user's sister Anna has her birthday on 4 March.", "sister  birthday!", "c2");

        Assert.Null(first);
        Assert.NotNull(replaced);
        Assert.Contains("3 March", replaced.Text);
        var all = store.All();
        Assert.Single(all);
        Assert.Contains("4 March", all[0].Text);
        Assert.Equal("c2", all[0].Conversation);
        Assert.Equal(saved.Id, all[0].Id);
    }

    [Fact]
    public void Search_finds_facts_by_word_forms_and_ignores_stop_words()
    {
        using var env = new TestEnv();
        var store = new MemoryStore(env.Db);
        store.Remember("The user's sister Anna has her birthday on 3 March.", "sister birthday", null);
        store.Remember("The user's car is a blue Skoda.", "car", null);
        store.Remember("The user works as a nurse.", "job", null);

        var hits = store.Search("When is Anna's birthday?");
        Assert.Single(hits);
        Assert.Contains("Anna", hits[0].Text);
        Assert.Contains("Skoda", store.Search("which cars do I have")[0].Text);
        Assert.Empty(store.Search("what is the"));
        Assert.Empty(store.Search("holiday"));
    }

    [Fact]
    public void Search_survives_quotes_and_fts_syntax_in_the_request()
    {
        using var env = new TestEnv();
        var store = new MemoryStore(env.Db);
        store.Remember("The user's server is called atlas.", "server name", null);

        Assert.Single(store.Search("\"atlas\" AND (NEAR OR *"));
    }

    [Fact]
    public void Forget_removes_from_search_and_forget_conversation_removes_only_that_conversation()
    {
        using var env = new TestEnv();
        var store = new MemoryStore(env.Db);
        var a = store.Remember("The user likes green tea.", "tea", "c1").Saved;
        store.Remember("The user's dog is called Rex.", "dog", "c1");
        store.Remember("The user lives in Leipzig.", "home town", "c2");

        Assert.True(store.Forget(a.Id));
        Assert.False(store.Forget(a.Id));
        Assert.Empty(store.Search("green tea"));

        Assert.Equal(1, store.ForgetConversation("c1"));
        var left = Assert.Single(store.All());
        Assert.Contains("Leipzig", left.Text);
    }

    [Fact]
    public void Fts_query_quotes_words_and_drops_stop_words()
    {
        Assert.Equal("\"anna\"* OR \"birthday\"*", MemoryStore.FtsQuery("When is Anna's birthday?"));
        Assert.Null(MemoryStore.FtsQuery("what is it?"));
        Assert.Equal("sister birthday", MemoryStore.NormalizeTopic("  Sister's  BIRTHDAY!"));
    }

    [Fact]
    public async Task Tools_save_with_the_turns_conversation_and_feed_the_prompt()
    {
        using var env = new TestEnv();
        var memory = new MemoryTools(new MemoryStore(env.Db));
        using var tracking = memory.TrackTurns(env.Events);
        env.Events.Publish(new TurnStarted("t1", "conv-1", "my dog is Rex", "chat"));
        env.Registry.Add(memory);
        var dispatcher = env.Dispatcher(new FixedApprovals(ApprovalAnswer.Yes));

        var saved = await dispatcher.InvokeAsync("memory_remember",
            new Dictionary<string, object?> { ["text"] = "The user's dog is called Rex.", ["topic"] = "dog" }, "t1", "chat", default);
        Assert.True(saved.Ok, saved.Text);

        var block = await memory.ContributeAsync("what's my dog's name", default);
        Assert.Contains("Rex", block);
        Assert.Null(await memory.ContributeAsync("weather tomorrow", default));

        Assert.Equal("conv-1", Assert.Single(new MemoryStore(env.Db).All()).Conversation);

        var recall = await dispatcher.InvokeAsync("memory_recall", new Dictionary<string, object?> { ["query"] = "dog" }, "t2", "chat", default);
        Assert.Contains("Rex", recall.Text);

        var forget = await dispatcher.InvokeAsync("memory_forget", new Dictionary<string, object?> { ["what"] = "dog" }, "t2", "chat", default);
        Assert.True(forget.Ok, forget.Text);
        Assert.Null(await memory.ContributeAsync("what's my dog's name", default));
    }

    [Fact]
    public async Task Forgetting_asks_first()
    {
        using var env = new TestEnv();
        var store = new MemoryStore(env.Db);
        store.Remember("The user's dog is called Rex.", "dog", null);
        env.Registry.Add(new MemoryTools(store));
        var approvals = new FixedApprovals(ApprovalAnswer.No);

        var result = await env.Dispatcher(approvals).InvokeAsync("memory_forget", new Dictionary<string, object?> { ["what"] = "dog" }, "t1", "chat", default);

        Assert.Equal(1, approvals.Asked);
        Assert.False(result.Ok);
        Assert.Single(store.All());
    }
}
