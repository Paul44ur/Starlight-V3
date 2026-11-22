using Serilog;
using Starlight.Events;
using Starlight.Settings;

namespace Starlight.Tools.Network;

/// <summary>
/// The optional watcher (plan 5.9): servers with a watch interval are checked (their ports and health address),
/// and the user hears when one goes down or comes back.
/// </summary>
public sealed class ServerWatcher(SettingsStore settings, INetProbe probe, EventHub events) : IDisposable
{
    private readonly Dictionary<string, (DateTimeOffset Last, bool Up)> _state = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public void Start() => _loop = Task.Run(async () =>
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                await TickAsync(DateTimeOffset.UtcNow, _stop.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warning(ex, "Server watch failed");
            }
        }
        while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false));
    });

    /// <summary>Checks the servers that are due. Returns the messages it published.</summary>
    public async Task<List<string>> TickAsync(DateTimeOffset now, CancellationToken ct)
    {
        var said = new List<string>();
        foreach (var server in settings.Current.Servers.Where(s => s.WatchMinutes > 0 && (s.WatchPorts.Count > 0 || s.HealthUrl.Length > 0)))
        {
            var known = _state.TryGetValue(server.Host, out var st);
            if (known && now - st.Last < TimeSpan.FromMinutes(server.WatchMinutes))
            {
                continue;
            }
            var failures = new List<string>();
            foreach (var port in server.WatchPorts)
            {
                var r = await probe.PortAsync(server.Host, port, ct);
                if (!r.Ok)
                {
                    failures.Add(r.Text);
                }
            }
            if (Uri.TryCreate(server.HealthUrl, UriKind.Absolute, out var url))
            {
                var r = await probe.HttpAsync(url, ct);
                if (!r.Ok)
                {
                    failures.Add(r.Text);
                }
            }
            var up = failures.Count == 0;
            _state[server.Host] = (now, up);
            // The first check only learns the state, unless the server is already down.
            if (known && st.Up == up || !known && up)
            {
                continue;
            }
            var text = up ? $"{server.Display} is back up." : $"{server.Display} is down: {string.Join("; ", failures)}.";
            Log.Information("Watcher: {Text}", text);
            events.Publish(new Notice(text, up ? NoticeLevel.Info : NoticeLevel.Warning));
            said.Add(text);
        }
        return said;
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }
        _stop.Dispose();
    }
}
