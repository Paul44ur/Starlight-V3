using Serilog;

namespace Starlight.Events;

/// <summary>In-process publish/subscribe. The overlay, voice and chat window only listen; they never call tools.</summary>
public sealed class EventHub
{
    private readonly Lock _gate = new();
    private List<Action<StarlightEvent>> _handlers = [];

    public IDisposable Subscribe(Action<StarlightEvent> handler)
    {
        lock (_gate)
        {
            _handlers = [.. _handlers, handler];
        }
        return new Unsubscriber(() =>
        {
            lock (_gate)
            {
                _handlers = _handlers.Where(h => h != handler).ToList();
            }
        });
    }

    public IDisposable Subscribe<T>(Action<T> handler) where T : StarlightEvent =>
        Subscribe(e => { if (e is T t) handler(t); });

    public void Publish(StarlightEvent e)
    {
        foreach (var h in _handlers)
        {
            try
            {
                h(e);
            }
            catch (Exception ex)
            {
                // A broken listener must never break the publisher.
                Log.Error(ex, "Event handler failed for {Event}", e.GetType().Name);
            }
        }
    }

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
