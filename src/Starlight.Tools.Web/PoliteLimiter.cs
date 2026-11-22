namespace Starlight.Tools.Web;

/// <summary>
/// Keeps Starlight polite on the web (plan 5.8): at most N requests per second overall (spaced 1/N seconds apart)
/// and one per second per site. <see cref="Reserve"/> books the next free slot and says how long to wait for it.
/// </summary>
public sealed class PoliteLimiter(Func<int> perSecond, TimeProvider? clock = null)
{
    private static readonly TimeSpan PerSite = TimeSpan.FromSeconds(1);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<string, DateTimeOffset> _nextForSite = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DateTimeOffset> _booked = [];
    private readonly Lock _lock = new();

    /// <summary>Books a slot for the site and returns the wait before using it.</summary>
    public TimeSpan Reserve(string host)
    {
        lock (_lock)
        {
            var now = _clock.GetUtcNow();
            var gap = TimeSpan.FromSeconds(1.0 / Math.Max(1, perSecond()));
            _booked.RemoveAll(b => b < now - gap);
            var slot = _nextForSite.TryGetValue(host, out var site) && site > now ? site : now;
            // Earliest time that is at least one gap away from every booking (the list is sorted).
            foreach (var b in _booked)
            {
                if (b - slot >= gap)
                {
                    break;
                }
                if (slot - b < gap)
                {
                    slot = b + gap;
                }
            }
            var at = _booked.FindIndex(b => b > slot);
            _booked.Insert(at < 0 ? _booked.Count : at, slot);
            _nextForSite[host] = slot + PerSite;
            if (_nextForSite.Count > 500)
            {
                foreach (var old in _nextForSite.Where(kv => kv.Value < now).Select(kv => kv.Key).ToList())
                {
                    _nextForSite.Remove(old);
                }
            }
            return slot - now;
        }
    }

    public async Task WaitAsync(Uri uri, CancellationToken ct)
    {
        var wait = Reserve(uri.Host);
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, _clock, ct);
        }
    }
}
