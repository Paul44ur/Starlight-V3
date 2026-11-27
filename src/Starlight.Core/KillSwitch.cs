using Serilog;
using Starlight.Events;

namespace Starlight;

/// <summary>
/// One key stops everything. Engaging cancels the shared token (tools, the code sandbox and SSH commands
/// stop on it), runs every cleanup callback (release keys, hide the pointer, stop speech) and stays engaged
/// until resumed. A cleanup that hangs is left behind after a short wait, so it can't hold up the others.
/// </summary>
public sealed class KillSwitch(EventHub events) : IDisposable
{
    private readonly Lock _gate = new();
    private CancellationTokenSource _cts = new();
    private readonly List<(string Name, Action Cleanup)> _cleanups = [];

    public bool IsEngaged { get; private set; }

    internal TimeSpan CleanupWait { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Token every task and tool links to. Replaced on resume.</summary>
    public CancellationToken Token
    {
        get { lock (_gate) { return _cts.Token; } }
    }

    public void RegisterCleanup(string name, Action cleanup)
    {
        lock (_gate)
        {
            _cleanups.Add((name, cleanup));
        }
    }

    public void Engage(string reason)
    {
        List<(string Name, Action Cleanup)> cleanups;
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (IsEngaged)
            {
                return;
            }
            IsEngaged = true;
            cts = _cts;
            cleanups = [.. _cleanups];
        }
        Log.Warning("Kill switch engaged: {Reason}", reason);
        try { cts.Cancel(); } catch (AggregateException ex) { Log.Error(ex, "A cancel callback failed"); }
        foreach (var (name, cleanup) in cleanups)
        {
            var run = Task.Run(() =>
            {
                try
                {
                    cleanup();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Kill-switch cleanup {Name} failed", name);
                }
            });
            if (!run.Wait(CleanupWait))
            {
                Log.Error("Kill-switch cleanup {Name} did not finish in {Ms} ms", name, CleanupWait.TotalMilliseconds);
            }
        }
        events.Publish(new KillSwitchChanged(true, reason));
        events.Publish(new StateChanged(AssistantState.Killed, reason));
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (!IsEngaged)
            {
                return;
            }
            _cts.Dispose();
            _cts = new CancellationTokenSource();
            IsEngaged = false;
        }
        Log.Information("Kill switch released");
        events.Publish(new KillSwitchChanged(false, "resumed"));
        events.Publish(new StateChanged(AssistantState.Idle));
    }

    /// <summary>Throws if engaged. Tools that loop call this between steps.</summary>
    public void ThrowIfEngaged()
    {
        if (IsEngaged)
        {
            throw new OperationCanceledException("The kill switch is engaged.");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _cts.Dispose();
        }
    }
}
