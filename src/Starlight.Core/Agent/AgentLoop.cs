using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.AI;
using Serilog;
using Starlight.Diagnostics;
using Starlight.Events;
using Starlight.Settings;
using Starlight.Storage;
using Starlight.Tools;

namespace Starlight.Agent;

public sealed record TurnOutcome(string TurnId, string Answer, bool Failed);

/// <summary>
/// One turn: build the prompt, stream the model's answer, run its tool calls through the dispatcher,
/// loop until it answers or MaxSteps. Only one turn runs at a time.
/// </summary>
public sealed class AgentLoop(
    Func<IChatClient?> chatClient,
    ToolRegistry tools,
    ToolDispatcher dispatcher,
    SettingsStore settings,
    KillSwitch killSwitch,
    EventHub events,
    SessionTrace trace,
    StarlightDb db,
    IEnumerable<IPromptContributor> contributors,
    IEnumerable<ITurnObserver> observers)
{
    private readonly SemaphoreSlim _oneTurn = new(1, 1);
    private CancellationTokenSource? _current;

    public bool Busy => _oneTurn.CurrentCount == 0;

    /// <summary>Stops the running turn (the user said "stop"), without engaging the kill switch.</summary>
    public void CancelCurrent() => _current?.Cancel();

    public async Task<TurnOutcome> RunAsync(Conversation conversation, string userText, string source, CancellationToken ct = default, IList<AIContent>? attachments = null)
    {
        var turnId = Guid.NewGuid().ToString("N")[..12];
        await _oneTurn.WaitAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, killSwitch.Token);
        _current = cts;
        var sw = Stopwatch.StartNew();
        events.Publish(new TurnStarted(turnId, conversation.Id, userText, source));
        events.Publish(new StateChanged(AssistantState.Thinking));
        trace.Write("turn.start", turnId, new { conversation = conversation.Id, source, text = userText });
        using var logScope = Serilog.Context.LogContext.PushProperty("Turn", turnId);
        try
        {
            if (killSwitch.IsEngaged)
            {
                return Finish(turnId, "I'm stopped. Resume me from the orb or the tray first.", failed: true);
            }
            var client = chatClient();
            if (client is null)
            {
                return Finish(turnId, "My model isn't running yet. It may still be starting; try again in a moment.", failed: true);
            }

            var userContents = new List<AIContent> { new TextContent(userText) };
            if (attachments is not null)
            {
                userContents.AddRange(attachments);
            }
            conversation.Messages.Add(new ChatMessage(ChatRole.User, userContents));
            if (conversation.Title == "New conversation")
            {
                conversation.Title = userText.Length <= 60 ? userText : userText[..57] + "…";
            }

            await CompactIfNeededAsync(client, conversation, turnId, cts.Token);
            var system = await BuildSystemPromptAsync(userText, source, cts.Token);
            var options = new ChatOptions
            {
                Tools = tools.Declarations(),
                ToolMode = ChatToolMode.Auto,
                Temperature = (float)settings.Current.Model.Temperature,
                AllowMultipleToolCalls = false,
            };

            var answer = new StringBuilder();
            var maxSteps = Math.Max(1, settings.Current.Model.MaxSteps);
            for (var step = 0; step < maxSteps; step++)
            {
                cts.Token.ThrowIfCancellationRequested();
                var messages = new List<ChatMessage>(conversation.Messages.Count + 1) { new(ChatRole.System, system) };
                messages.AddRange(conversation.Messages);

                var updates = new List<ChatResponseUpdate>();
                var stepText = new StringBuilder();
                var first = Stopwatch.StartNew();
                TimeSpan? ttft = null;
                await foreach (var u in client.GetStreamingResponseAsync(messages, options, cts.Token))
                {
                    updates.Add(u);
                    var delta = u.Text;
                    if (!string.IsNullOrEmpty(delta))
                    {
                        ttft ??= first.Elapsed;
                        stepText.Append(delta);
                        events.Publish(new AnswerDelta(turnId, delta));
                    }
                }
                var response = updates.ToChatResponse();
                RecordSpeed(response, first.Elapsed, ttft, stepText.Length);
                conversation.Messages.AddRange(response.Messages);

                var calls = response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToList();
                if (calls.Count == 0)
                {
                    answer.Append(stepText);
                    break;
                }
                if (stepText.Length > 0)
                {
                    answer.Append(stepText).Append(' ');
                }
                foreach (var call in calls)
                {
                    trace.Write("model.call", turnId, new { tool = call.Name, args = call.Arguments });
                    var result = await dispatcher.InvokeAsync(call.Name, call.Arguments, turnId, source, cts.Token);
                    conversation.Messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId, result.Ok ? result.Text : "ERROR: " + result.Text)]));
                    events.Publish(new StateChanged(AssistantState.Thinking));
                }
                if (step == maxSteps - 1)
                {
                    answer.Append("I stopped after ").Append(maxSteps).Append(" steps without finishing. Tell me how to continue.");
                }
            }

            var text = answer.ToString().Trim();
            if (text.Length == 0)
            {
                text = "Done.";
            }
            Save(conversation);
            foreach (var o in observers)
            {
                _ = ObserveAsync(o, conversation, userText, text);
            }
            db.Stat("turn.seconds", sw.Elapsed.TotalSeconds, source);
            return Finish(turnId, text, failed: false);
        }
        catch (OperationCanceledException)
        {
            Save(conversation);
            return Finish(turnId, killSwitch.IsEngaged ? "Stopped." : "Okay, stopped.", failed: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            Log.Error(ex, "Model call failed in turn {Turn}", turnId);
            Save(conversation);
            return Finish(turnId, "I couldn't reach my model. It may be restarting; try again in a moment.", failed: true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Turn {Turn} failed", turnId);
            Save(conversation);
            return Finish(turnId, $"Something went wrong on my side ({ex.GetType().Name}). It's in the error log.", failed: true);
        }
        finally
        {
            _current = null;
            _oneTurn.Release();
        }
    }

    private TurnOutcome Finish(string turnId, string text, bool failed)
    {
        trace.Write("turn.end", turnId, new { failed, text });
        events.Publish(new AnswerCompleted(turnId, text, failed));
        if (!killSwitch.IsEngaged)
        {
            events.Publish(new StateChanged(AssistantState.Idle));
        }
        return new TurnOutcome(turnId, text, failed);
    }

    private async Task<string> BuildSystemPromptAsync(string userText, string source, CancellationToken ct)
    {
        var blocks = new List<string>();
        foreach (var c in contributors)
        {
            try
            {
                var block = await c.ContributeAsync(userText, ct);
                if (!string.IsNullOrWhiteSpace(block))
                {
                    blocks.Add(block);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Error(ex, "Prompt contributor {Contributor} failed", c.GetType().Name);
            }
        }
        return SystemPrompt.Build(settings.Current, DateTimeOffset.Now, blocks, source);
    }

    /// <summary>When the conversation fills 75 % of the context (plan 5.1), older turns become one summary.</summary>
    private async Task CompactIfNeededAsync(IChatClient client, Conversation conversation, string turnId, CancellationToken ct)
    {
        var budget = settings.Current.Model.ContextSize * 0.75 - 3000; // room for the prompt and tool schemas
        if (conversation.EstimateTokens() < budget || conversation.Messages.Count < 8)
        {
            return;
        }
        // Keep the last 6 messages, but never split a tool call from its result.
        var keepFrom = conversation.Messages.Count - 6;
        while (keepFrom > 0 && conversation.Messages[keepFrom].Role == ChatRole.Tool)
        {
            keepFrom--;
        }
        var old = conversation.Messages.Take(keepFrom).ToList();
        var transcript = new StringBuilder();
        foreach (var m in old)
        {
            var text = string.Join(" ", m.Contents.Select(c => c switch
            {
                TextContent t => t.Text,
                FunctionCallContent f => $"[called {f.Name}]",
                FunctionResultContent r => $"[result: {Clip(r.Result?.ToString(), 300)}]",
                _ => "",
            }));
            transcript.Append(m.Role.Value).Append(": ").AppendLine(Clip(text, 2000));
        }
        var summary = await client.GetResponseAsync(
            [
                new ChatMessage(ChatRole.System, "Summarize this conversation in at most 12 short bullet points. Keep names, numbers, decisions, open tasks and anything the user asked to remember."),
                new ChatMessage(ChatRole.User, transcript.ToString()),
            ], new ChatOptions { Temperature = 0.2f }, ct);
        conversation.Messages.RemoveRange(0, keepFrom);
        conversation.Messages.Insert(0, new ChatMessage(ChatRole.User, "Summary of our earlier conversation:\n" + summary.Text));
        conversation.Messages.Insert(1, new ChatMessage(ChatRole.Assistant, "Understood."));
        trace.Write("context.compacted", turnId, new { removed = keepFrom });
        Log.Information("Compacted conversation {Id}: {Removed} messages summarized", conversation.Id, keepFrom);
    }

    private static string Clip(string? s, int max) => s is null ? "" : s.Length <= max ? s : s[..max] + "…";

    private void RecordSpeed(ChatResponse response, TimeSpan total, TimeSpan? ttft, int chars)
    {
        try
        {
            var outTokens = response.Usage?.OutputTokenCount;
            var genSeconds = (total - (ttft ?? TimeSpan.Zero)).TotalSeconds;
            if (outTokens is > 0 && genSeconds > 0.05)
            {
                db.Stat("model.tokens_per_second", outTokens.Value / genSeconds);
            }
            else if (chars > 20 && genSeconds > 0.05)
            {
                db.Stat("model.tokens_per_second", chars / 3.5 / genSeconds, "estimated");
            }
            if (ttft is { } t)
            {
                db.Stat("model.first_token_seconds", t.TotalSeconds);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not record model speed");
        }
    }

    private void Save(Conversation conversation)
    {
        try
        {
            db.SaveConversation(conversation.Id, conversation.Title, conversation.Messages, conversation.Created);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not save conversation {Id}", conversation.Id);
        }
    }

    private async Task ObserveAsync(ITurnObserver observer, Conversation conversation, string userText, string answer)
    {
        try
        {
            await observer.OnTurnCompletedAsync(conversation, userText, answer, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Turn observer {Observer} failed", observer.GetType().Name);
        }
    }
}
