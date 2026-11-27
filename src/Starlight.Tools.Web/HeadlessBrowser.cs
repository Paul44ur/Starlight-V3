using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Serilog;
using Starlight.Platform;

namespace Starlight.Tools.Web;

/// <summary>A cookie as the DevTools protocol gives it, kept for a site after a login.</summary>
public sealed record BrowserCookie(string Name, string Value, string Domain, string Path, double Expires, bool HttpOnly, bool Secure, string? SameSite);

/// <summary>The browser work the web tools need; the real one drives Edge, tests use a fake.</summary>
public interface IBrowser
{
    /// <summary>Opens the page, lets its scripts run, and returns the final address and HTML.</summary>
    Task<(Uri Url, string Html)> RenderAsync(Uri url, IReadOnlyList<BrowserCookie> cookies, CancellationToken ct);

    /// <summary>Opens a page and runs a script in it, then returns the script's value and the page's cookies.</summary>
    Task<IBrowserSession> OpenAsync(Uri url, IReadOnlyList<BrowserCookie> cookies, CancellationToken ct);
}

public interface IBrowserSession : IAsyncDisposable
{
    Task<string> EvaluateAsync(string script, CancellationToken ct);
    Task WaitForLoadAsync(TimeSpan max, CancellationToken ct);
    Task<IReadOnlyList<BrowserCookie>> CookiesAsync(CancellationToken ct);
    Uri Url { get; }
}

/// <summary>
/// The headless browser (R4): the Microsoft Edge that comes with Windows 11, started with its own profile in a
/// Job Object and driven over the DevTools protocol. No Node.js driver and no browser download.
/// </summary>
public sealed class EdgeBrowser(string profileDir, string? exe = null) : IBrowser, IAsyncDisposable
{
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private Process? _process;
    private JobObject? _job;
    private Cdp? _cdp;
    private FilterProxy? _proxy;

    /// <summary>More browser flags; tests on Linux need --no-sandbox when running as root.</summary>
    internal string[] ExtraArgs { get; init; } = [];

    /// <summary>Decides for every request the page makes, redirects and images included. Public web only by default.</summary>
    internal Func<Uri, CancellationToken, Task<bool>> Allow { get; init; } = UrlGuard.IsPublicAsync;

    /// <summary>How the browser's own proxy connects. The default resolves once and connects to the checked
    /// public address, which closes the DNS-rebinding gap between <see cref="Allow"/> and the connection.</summary>
    internal Func<string, int, CancellationToken, ValueTask<Stream>> Connect { get; init; } = UrlGuard.ConnectPublicAsync;

    public static string? FindExe()
    {
        string[] roots = [Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)];
        string[] relative = [@"Microsoft\Edge\Application\msedge.exe", @"Google\Chrome\Application\chrome.exe"];
        return relative.SelectMany(r => roots.Where(x => x.Length > 0).Select(root => Path.Combine(root, r))).FirstOrDefault(File.Exists);
    }

    public async Task<(Uri Url, string Html)> RenderAsync(Uri url, IReadOnlyList<BrowserCookie> cookies, CancellationToken ct)
    {
        await using var page = await OpenAsync(url, cookies, ct);
        await Task.Delay(1200, ct); // let scripts fill the page
        var html = await page.EvaluateAsync("document.documentElement.outerHTML", ct);
        return (page.Url, html);
    }

    public async Task<IBrowserSession> OpenAsync(Uri url, IReadOnlyList<BrowserCookie> cookies, CancellationToken ct)
    {
        var cdp = await StartAsync(ct);
        var target = await cdp.SendAsync("Target.createTarget", new JsonObject { ["url"] = "about:blank" }, null, ct);
        var targetId = target["targetId"]!.GetValue<string>();
        var attached = await cdp.SendAsync("Target.attachToTarget", new JsonObject { ["targetId"] = targetId, ["flatten"] = true }, null, ct);
        var page = new CdpPage(cdp, targetId, attached["sessionId"]!.GetValue<string>(), url, Allow);
        try
        {
            await page.InitAsync(cookies, ct);
            await page.NavigateAsync(url, ct);
            return page;
        }
        catch
        {
            // The tab and its request handler must not stay behind.
            await page.DisposeAsync();
            throw;
        }
    }

    private async Task<Cdp> StartAsync(CancellationToken ct)
    {
        await _startLock.WaitAsync(ct);
        try
        {
            if (_cdp is { IsOpen: true } && _process is { HasExited: false })
            {
                return _cdp;
            }
            await StopAsync();
            var path = exe ?? FindExe() ?? throw new ToolException("Microsoft Edge was not found, so pages that need scripts can't be read.");
            Directory.CreateDirectory(profileDir);
            var portFile = Path.Combine(profileDir, "DevToolsActivePort");
            File.Delete(portFile);
            _proxy ??= new FilterProxy(Connect);
            string[] args =
            [
                .. ExtraArgs,
                // Every connection goes through the filter, loopback too ("<-loopback>" removes the built-in bypass).
                $"--proxy-server=http://127.0.0.1:{_proxy.Port}", "--proxy-bypass-list=<-loopback>",
                "--headless=new", "--remote-debugging-port=0", "--user-data-dir=" + profileDir, "--no-first-run", "--no-default-browser-check",
                "--disable-extensions", "--disable-sync", "--disable-features=msEdgeSidebarV2,EdgeCollections", "--mute-audio", "--disable-background-networking", "about:blank",
            ];
            // Started inside the job, so the renderer and GPU processes Edge starts at once are in it too.
            _job ??= new JobObject();
            _process = _job.Start(path, args);
            var deadline = DateTime.UtcNow.AddSeconds(15);
            string[]? lines = null;
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(portFile))
                {
                    try
                    {
                        lines = (await File.ReadAllLinesAsync(portFile, ct)).Where(l => l.Length > 0).ToArray();
                        if (lines.Length >= 2)
                        {
                            break;
                        }
                    }
                    catch (IOException)
                    {
                    }
                }
                if (_process.HasExited)
                {
                    throw new ToolException("The browser closed right after starting.");
                }
                await Task.Delay(100, ct);
            }
            if (lines is not { Length: >= 2 })
            {
                throw new ToolException("The browser did not open its control port in 15 seconds.");
            }
            _cdp = await Cdp.ConnectAsync(new Uri($"ws://127.0.0.1:{lines[0]}{lines[1]}"), ct);
            Log.Information("Headless browser started: {Exe}", path);
            return _cdp;
        }
        finally
        {
            _startLock.Release();
        }
    }

    private async Task StopAsync()
    {
        if (_cdp is not null)
        {
            try
            {
                using var cts = new CancellationTokenSource(2000);
                await _cdp.SendAsync("Browser.close", null, null, cts.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ToolException or InvalidOperationException)
            {
            }
            await _cdp.DisposeAsync();
            _cdp = null;
        }
        if (_process is { HasExited: false })
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        }
        _process?.Dispose();
        _process = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        if (_proxy is not null)
        {
            await _proxy.DisposeAsync();
        }
        _job?.Dispose();
        _startLock.Dispose();
    }
}

internal sealed class CdpPage(Cdp cdp, string targetId, string sessionId, Uri url, Func<Uri, CancellationToken, Task<bool>> allow) : IBrowserSession
{
    public Uri Url { get; private set; } = url;

    public async Task InitAsync(IReadOnlyList<BrowserCookie> cookies, CancellationToken ct)
    {
        // Every request waits here until it is checked: a page cannot reach this PC or the home network,
        // not through a redirect, an image or a script either.
        _unsubscribe = cdp.On("Fetch.requestPaused", sessionId, GuardAsync);
        await cdp.SendAsync("Fetch.enable", new JsonObject { ["patterns"] = new JsonArray(new JsonObject { ["urlPattern"] = "*" }) }, sessionId, ct);
        await cdp.SendAsync("Page.enable", null, sessionId, ct);
        await cdp.SendAsync("Network.enable", null, sessionId, ct);
        if (cookies.Count > 0)
        {
            var list = new JsonArray();
            foreach (var c in cookies)
            {
                var o = new JsonObject
                {
                    ["name"] = c.Name, ["value"] = c.Value, ["domain"] = c.Domain, ["path"] = c.Path,
                    ["httpOnly"] = c.HttpOnly, ["secure"] = c.Secure,
                };
                if (c.Expires > 0)
                {
                    o["expires"] = c.Expires;
                }
                if (c.SameSite is { Length: > 0 } s)
                {
                    o["sameSite"] = s;
                }
                list.Add(o);
            }
            await cdp.SendAsync("Network.setCookies", new JsonObject { ["cookies"] = list }, sessionId, ct);
        }
    }

    private IDisposable? _unsubscribe;

    /// <summary>The addresses this page was not allowed to reach.</summary>
    public ConcurrentQueue<string> Blocked { get; } = new();

    private async Task GuardAsync(JsonObject e)
    {
        var id = e["requestId"]!.GetValue<string>();
        var url = e["request"]?["url"]?.GetValue<string>();
        bool ok;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            ok = Uri.TryCreate(url, UriKind.Absolute, out var u) && await allow(u, cts.Token);
        }
        catch (OperationCanceledException)
        {
            ok = false;
        }
        if (!ok)
        {
            Blocked.Enqueue(url ?? "");
            Log.Warning("Browser request blocked: {Url}", url);
        }
        try
        {
            await (ok
                ? cdp.SendAsync("Fetch.continueRequest", new JsonObject { ["requestId"] = id }, sessionId, default)
                : cdp.SendAsync("Fetch.failRequest", new JsonObject { ["requestId"] = id, ["errorReason"] = "AccessDenied" }, sessionId, default));
        }
        catch (ToolException)
        {
            // The page went away while the request was checked.
        }
    }

    public async Task NavigateAsync(Uri to, CancellationToken ct)
    {
        var loaded = cdp.WaitForEvent("Page.loadEventFired", sessionId);
        var nav = await cdp.SendAsync("Page.navigate", new JsonObject { ["url"] = to.ToString() }, sessionId, ct);
        if (nav["errorText"]?.GetValue<string>() is { Length: > 0 } error)
        {
            if (!Blocked.IsEmpty)
            {
                throw new ToolException($"{to.Host} sent the browser to an address on this PC or the local network, so it was stopped.");
            }
            throw new ToolException($"The browser could not open {to.Host}: {error}");
        }
        await WaitAsync(loaded, TimeSpan.FromSeconds(20), ct);
        await RefreshUrlAsync(ct);
    }

    public async Task WaitForLoadAsync(TimeSpan max, CancellationToken ct)
    {
        await WaitAsync(cdp.WaitForEvent("Page.loadEventFired", sessionId), max, ct);
        await RefreshUrlAsync(ct);
    }

    private static async Task WaitAsync(Task loaded, TimeSpan max, CancellationToken ct)
    {
        // A slow page is read as far as it got.
        await Task.WhenAny(loaded, Task.Delay(max, ct));
        ct.ThrowIfCancellationRequested();
    }

    private async Task RefreshUrlAsync(CancellationToken ct)
    {
        if (Uri.TryCreate(await EvaluateAsync("location.href", ct), UriKind.Absolute, out var u))
        {
            Url = u;
        }
    }

    public async Task<string> EvaluateAsync(string script, CancellationToken ct)
    {
        var r = await cdp.SendAsync("Runtime.evaluate", new JsonObject { ["expression"] = script, ["returnByValue"] = true, ["awaitPromise"] = true }, sessionId, ct);
        if (r["exceptionDetails"] is { } ex)
        {
            throw new ToolException("A script in the page failed: " + (ex["exception"]?["description"]?.GetValue<string>() ?? ex["text"]?.GetValue<string>() ?? "unknown"));
        }
        var value = r["result"]?["value"];
        return value switch
        {
            null => "",
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            _ => value.ToJsonString(),
        };
    }

    public async Task<IReadOnlyList<BrowserCookie>> CookiesAsync(CancellationToken ct)
    {
        // Only the cookies for this page's address, not every cookie in the shared browser profile.
        var r = await cdp.SendAsync("Network.getCookies", new JsonObject { ["urls"] = new JsonArray(Url.AbsoluteUri) }, sessionId, ct);
        return r["cookies"]?.AsArray().Select(c => new BrowserCookie(
            c!["name"]!.GetValue<string>(), c["value"]!.GetValue<string>(), c["domain"]!.GetValue<string>(), c["path"]!.GetValue<string>(),
            c["expires"]?.GetValue<double>() ?? -1, c["httpOnly"]?.GetValue<bool>() ?? false, c["secure"]?.GetValue<bool>() ?? false,
            c["sameSite"]?.GetValue<string>())).ToList() ?? [];
    }

    public async ValueTask DisposeAsync()
    {
        _unsubscribe?.Dispose();
        try
        {
            using var cts = new CancellationTokenSource(3000);
            await cdp.SendAsync("Target.closeTarget", new JsonObject { ["targetId"] = targetId }, null, cts.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ToolException or InvalidOperationException)
        {
        }
    }
}

/// <summary>A small DevTools protocol client: numbered requests, replies by number, events by name.</summary>
internal sealed class Cdp : IAsyncDisposable
{
    private readonly ClientWebSocket _ws;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly ConcurrentDictionary<Guid, (string Method, string? Session, TaskCompletionSource Done)> _waiters = new();
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _reader;
    private int _id;

    private Cdp(ClientWebSocket ws)
    {
        _ws = ws;
        _reader = Task.Run(ReadLoopAsync);
    }

    public bool IsOpen => _ws.State == WebSocketState.Open;

    public static async Task<Cdp> ConnectAsync(Uri uri, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        await ws.ConnectAsync(uri, ct);
        return new Cdp(ws);
    }

    private readonly ConcurrentDictionary<Guid, (string Method, string? Session, Func<JsonObject, Task> Handler)> _handlers = new();

    /// <summary>Calls the handler for every such event, off the reading loop. Dispose the result to stop.</summary>
    public IDisposable On(string method, string? session, Func<JsonObject, Task> handler)
    {
        var key = Guid.NewGuid();
        _handlers[key] = (method, session, handler);
        return new Unsubscribe(() => _handlers.TryRemove(key, out _));
    }

    private sealed class Unsubscribe(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    public Task WaitForEvent(string method, string? session)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var key = Guid.NewGuid();
        _waiters[key] = (method, session, tcs);
        _ = tcs.Task.ContinueWith(_ => _waiters.TryRemove(key, out var _), TaskScheduler.Default);
        // Waiters that never fire are dropped after a minute.
        _ = Task.Delay(TimeSpan.FromMinutes(1)).ContinueWith(_ => tcs.TrySetCanceled(), TaskScheduler.Default);
        return tcs.Task;
    }

    public async Task<JsonObject> SendAsync(string method, JsonObject? parameters, string? session, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _id);
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var message = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters ?? new JsonObject() };
        if (session is not null)
        {
            message["sessionId"] = session;
        }
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await _send.WaitAsync(ct);
        try
        {
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _send.Release();
        }
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        try
        {
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        }
        catch (TimeoutException)
        {
            throw new ToolException($"The browser did not answer {method}.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[1 << 16];
        using var message = new MemoryStream();
        try
        {
            while (_ws.State == WebSocketState.Open && !_stop.IsCancellationRequested)
            {
                var r = await _ws.ReceiveAsync(buffer, _stop.Token);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
                message.Write(buffer, 0, r.Count);
                if (!r.EndOfMessage)
                {
                    continue;
                }
                JsonObject? json = null;
                try
                {
                    json = JsonNode.Parse(message.GetBuffer().AsSpan(0, (int)message.Length))?.AsObject();
                }
                catch (JsonException)
                {
                }
                message.SetLength(0);
                if (json is null)
                {
                    continue;
                }
                if (json["id"] is { } idNode && _pending.TryGetValue(idNode.GetValue<int>(), out var tcs))
                {
                    if (json["error"] is { } error)
                    {
                        tcs.TrySetException(new ToolException("Browser: " + (error["message"]?.GetValue<string>() ?? "error")));
                    }
                    else
                    {
                        tcs.TrySetResult(json["result"]?.AsObject() ?? new JsonObject());
                    }
                }
                else if (json["method"]?.GetValue<string>() is { } method)
                {
                    var session = json["sessionId"]?.GetValue<string>();
                    var parameters = json["params"]?.AsObject() ?? new JsonObject();
                    foreach (var h in _handlers.Values.Where(h => h.Method == method && (h.Session is null || h.Session == session)))
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await h.Handler(parameters);
                            }
                            catch (Exception ex)
                            {
                                Log.Warning(ex, "Browser event {Method} failed", method);
                            }
                        });
                    }
                    foreach (var w in _waiters.Values.Where(w => w.Method == method && (w.Session is null || w.Session == session)))
                    {
                        w.Done.TrySetResult();
                    }
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
        }
        foreach (var p in _pending.Values)
        {
            p.TrySetException(new ToolException("The browser closed."));
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            if (_ws.State == WebSocketState.Open)
            {
                using var cts = new CancellationTokenSource(1000);
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", cts.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
        }
        await _reader.ConfigureAwait(false);
        _ws.Dispose();
        _stop.Dispose();
        _send.Dispose();
    }
}
