using System.Diagnostics;
using Serilog;
using Starlight.Agent;
using Starlight.Events;
using Starlight.Settings;

namespace Starlight.Voice;

/// <summary>
/// Push-to-talk turn: listen while the key is held, write down what was said, run the turn,
/// speak the answer sentence by sentence. Pressing the key while Starlight talks stops it (barge-in).
/// </summary>
public sealed class VoiceLoop : IDisposable
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "stop", "cancel", "never mind", "nevermind", "be quiet", "shut up", "quiet", "stop it", "that's enough",
    };

    private readonly Microphone _mic;
    private readonly Recognizer _recognizer;
    private readonly Speaker _speaker;
    private readonly AgentLoop _agent;
    private readonly EventHub _events;
    private readonly SettingsStore _settings;
    private readonly IDisposable _deltaSub;
    private readonly IDisposable _killSub;
    /// <summary>The spoken turn whose answer is read aloud now. A turn that was cut off by a new one
    /// (barge-in) finds it replaced and leaves it alone.</summary>
    private sealed class SpokenTurn
    {
        public SentenceSplitter Splitter { get; } = new();
        public string? TurnId { get; set; }
    }

    private SpokenTurn? _current;
    private Task? _running;
    private WakeWord? _wake;
    private Endpointer? _endpointer;
    // Guards _endpointer between the capture thread (OnSamples), push to talk and the wake phrase.
    private readonly Lock _listenGate = new();
    private bool _waking;
    private DateTime _handsFreeSince;

    public VoiceLoop(Microphone mic, Recognizer recognizer, Speaker speaker, AgentLoop agent, EventHub events, SettingsStore settings)
    {
        _mic = mic;
        _recognizer = recognizer;
        _speaker = speaker;
        _agent = agent;
        _events = events;
        _settings = settings;
        _deltaSub = events.Subscribe<AnswerDelta>(OnDelta);
        _killSub = events.Subscribe<KillSwitchChanged>(k =>
        {
            if (k.Engaged)
            {
                _speaker.Stop();
                _mic.EndUtterance();
            }
        });
        _speaker.Finished += () =>
        {
            if (!_agent.Busy && !_mic.IsRecording)
            {
                _events.Publish(new StateChanged(AssistantState.Idle));
                ResumeWake();
            }
        };
        ApplySettings();
        settings.Changed += _ => ApplySettings();
    }

    /// <summary>Answers an open Yes/No card by voice. Returns true if a card was open. Set by the app.</summary>
    public Func<Starlight.Tools.ApprovalAnswer, bool>? AnswerApproval { get; set; }

    /// <summary>True while a Yes/No card is open (the turn waits for it, so listening must not cancel it).</summary>
    public Func<bool>? ApprovalPending { get; set; }

    /// <summary>Set by the app: a sign-in code is asked for, and the spoken digits answer it.</summary>
    public Func<bool>? CodePending { get; set; }
    public Action<string>? AnswerCode { get; set; }

    /// <summary>Gives the conversation a spoken request goes into (shared with the chat window).</summary>
    public Func<Conversation>? ConversationFor { get; set; }

    private Conversation _fallback = new();

    private void ApplySettings()
    {
        _speaker.Voice = _settings.Current.Voice.Voice;
        _speaker.Rate = (float)_settings.Current.Voice.SpeechRate;
    }

    /// <summary>Starts listening for the wake phrase all the time (plan P6).</summary>
    public void EnableWakeWord(WakeWord wake)
    {
        _wake = wake;
        wake.Heard += () => Task.Run(() =>
        {
            try
            {
                WakeUp();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Listening after the wake phrase failed");
                _events.Publish(new StateChanged(AssistantState.Idle));
                ResumeWake();
            }
        });
        _mic.Samples += OnSamples;
        _mic.Open();
    }

    private void OnSamples(float[] chunk)
    {
        var stop = false;
        lock (_listenGate)
        {
            if (_endpointer is { } ep)
            {
                var ended = ep.Feed(chunk);
                var waited = DateTime.UtcNow - _handsFreeSince;
                if (ended || waited > TimeSpan.FromSeconds(20) || (!ep.SpeechStarted && waited > TimeSpan.FromSeconds(5)))
                {
                    _endpointer = null;
                    ep.Dispose();
                    stop = true;
                }
                else
                {
                    return;
                }
            }
        }
        if (stop)
        {
            StopListening();
            return;
        }
        _wake?.Feed(chunk);
    }

    /// <summary>The wake phrase was heard: listen until the user stops talking. Two quick wake events start one listen.</summary>
    private void WakeUp()
    {
        lock (_listenGate)
        {
            if (_waking || _endpointer is not null || _mic.IsRecording)
            {
                return;
            }
            _waking = true;
        }
        try
        {
            if (_wake is not null)
            {
                _wake.Active = false;
            }
            StartListening();
            var ep = _recognizer.CreateEndpointer();
            lock (_listenGate)
            {
                _handsFreeSince = DateTime.UtcNow;
                _endpointer = ep;
            }
        }
        finally
        {
            lock (_listenGate)
            {
                _waking = false;
            }
        }
    }

    private void ResumeWake()
    {
        if (_wake is not null && !_wake.Active)
        {
            _wake.Reset();
            _wake.Active = true;
        }
    }

    /// <summary>The push-to-talk key went down.</summary>
    public void StartListening()
    {
        // Push to talk while a hands-free listen runs: the key decides when it ends, not the pause detector.
        lock (_listenGate)
        {
            _endpointer?.Dispose();
            _endpointer = null;
        }
        if (_wake is not null)
        {
            _wake.Active = false;
        }
        // Barge-in: the turn being spoken is cut off and must not speak its rest or its "cancelled" later.
        _current = null;
        if (_speaker.IsSpeaking)
        {
            _speaker.Stop();
        }
        if (_agent.Busy && ApprovalPending?.Invoke() != true)
        {
            _agent.CancelCurrent();
        }
        try
        {
            _mic.BeginUtterance();
        }
        catch
        {
            // No microphone: hands-free listening must not stay off because push to talk failed.
            ResumeWake();
            throw;
        }
        _events.Publish(new StateChanged(AssistantState.Listening));
    }

    /// <summary>The push-to-talk key came up: understand and answer.</summary>
    public void StopListening()
    {
        var audio = _mic.EndUtterance();
        _running = Task.Run(() => HandleAsync(audio));
    }

    private async Task HandleAsync(float[] audio)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            _events.Publish(new StateChanged(AssistantState.Thinking));
            var text = await _recognizer.TranscribeAsync(audio, CancellationToken.None);
            if (text.Length == 0)
            {
                _events.Publish(new StateChanged(AssistantState.Idle, "Nothing heard"));
                ResumeWake();
                return;
            }
            _events.Publish(new UserSaid(text, "voice"));
            if (ApprovalPending?.Invoke() == true && VoiceApproval.Parse(text) is { } answer)
            {
                AnswerApproval?.Invoke(answer);
                ResumeWake();
                return;
            }
            if (CodePending?.Invoke() == true && SpokenCode.Parse(text) is { } code)
            {
                AnswerCode?.Invoke(code);
                ResumeWake();
                return;
            }
            if (StopWords.Contains(text.Trim().TrimEnd('.', '!').Trim()))
            {
                _speaker.Stop();
                _agent.CancelCurrent();
                _events.Publish(new StateChanged(AssistantState.Idle));
                ResumeWake();
                return;
            }
            var turn = new SpokenTurn();
            _current = turn;
            var conversation = ConversationFor?.Invoke() ?? _fallback;
            var heardAt = sw.Elapsed;
            var outcome = await _agent.RunAsync(conversation, text, "voice");
            if (Interlocked.CompareExchange(ref _current, null, turn) != turn)
            {
                // A newer turn took over (barge-in): it speaks, this one stays quiet.
                return;
            }
            if (turn.Splitter.Flush() is { } rest)
            {
                Say(rest);
            }
            if (outcome.Failed && !string.IsNullOrWhiteSpace(outcome.Answer))
            {
                Say(outcome.Answer); // failures are not streamed, so speak them here
            }
            Log.Information("Voice turn: heard after {Heard:0.00} s, done after {Total:0.00} s", heardAt.TotalSeconds, sw.Elapsed.TotalSeconds);
            if (_speaker.IsSpeaking)
            {
                _events.Publish(new StateChanged(AssistantState.Speaking));
            }
            else
            {
                ResumeWake();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Voice turn failed");
            _events.Publish(new StateChanged(AssistantState.Idle));
            ResumeWake();
        }
    }

    private void OnDelta(AnswerDelta d)
    {
        var turn = _current;
        if (turn is null)
        {
            return;
        }
        lock (turn)
        {
            turn.TurnId ??= d.TurnId;
            if (turn.TurnId != d.TurnId)
            {
                return;
            }
        }
        foreach (var sentence in turn.Splitter.Add(d.Delta))
        {
            Say(sentence);
        }
    }

    private void Say(string sentence)
    {
        if (!_settings.Current.Voice.SpeakAnswers)
        {
            return;
        }
        _speaker.Say(sentence);
        _events.Publish(new StateChanged(AssistantState.Speaking));
    }

    public void Dispose()
    {
        _mic.Samples -= OnSamples;
        lock (_listenGate)
        {
            _endpointer?.Dispose();
            _endpointer = null;
        }
        _deltaSub.Dispose();
        _killSub.Dispose();
        _running?.Wait(TimeSpan.FromSeconds(2));
    }
}
