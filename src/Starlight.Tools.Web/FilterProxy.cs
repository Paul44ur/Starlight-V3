using System.Net;
using System.Net.Sockets;
using System.Text;
using Serilog;

namespace Starlight.Tools.Web;

/// <summary>
/// A small HTTP proxy on 127.0.0.1 that the headless browser must use for every connection. The browser
/// never resolves names itself: this proxy resolves each host once, checks the address and connects to that
/// same address. So a name that turns local between the check and the connection (DNS rebinding) is refused.
/// </summary>
internal sealed class FilterProxy : IAsyncDisposable
{
    private const int MaxHeaderBytes = 64 * 1024;
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Func<string, int, CancellationToken, ValueTask<Stream>> _connect;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public FilterProxy(Func<string, int, CancellationToken, ValueTask<Stream>> connect)
    {
        _connect = connect;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using var _ = client;
        var ct = _stop.Token;
        try
        {
            var stream = client.GetStream();
            var head = await ReadHeadAsync(stream, ct);
            if (head is null)
            {
                return;
            }
            var lines = head.Split("\r\n");
            var parts = lines[0].Split(' ');
            if (parts.Length != 3)
            {
                await ReplyAsync(stream, "400 Bad Request", ct);
                return;
            }
            var (method, target) = (parts[0], parts[1]);
            Stream upstream;
            string? forward = null;
            if (method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
            {
                if (!TrySplitHostPort(target, 443, out var host, out var port))
                {
                    await ReplyAsync(stream, "400 Bad Request", ct);
                    return;
                }
                upstream = await ConnectAsync(stream, host, port, ct);
                if (ReferenceEquals(upstream, Stream.Null))
                {
                    return;
                }
                await stream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), ct);
            }
            else
            {
                if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || uri.Scheme != "http")
                {
                    await ReplyAsync(stream, "400 Bad Request", ct);
                    return;
                }
                upstream = await ConnectAsync(stream, uri.Host, uri.Port, ct);
                if (ReferenceEquals(upstream, Stream.Null))
                {
                    return;
                }
                // One request per connection: the next one may be for another host and must be checked again.
                var sb = new StringBuilder($"{method} {uri.PathAndQuery} {parts[2]}\r\n");
                foreach (var line in lines.Skip(1).Where(l => l.Length > 0))
                {
                    var name = line.Split(':', 2)[0].Trim();
                    if (!name.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase) && !name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                    {
                        sb.Append(line).Append("\r\n");
                    }
                }
                sb.Append("Connection: close\r\n\r\n");
                forward = sb.ToString();
            }
            await using (upstream)
            {
                if (forward is not null)
                {
                    await upstream.WriteAsync(Encoding.ASCII.GetBytes(forward), ct);
                }
                using var done = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var up = stream.CopyToAsync(upstream, done.Token);
                var down = upstream.CopyToAsync(stream, done.Token);
                await Task.WhenAny(up, down);
                await done.CancelAsync();
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    /// <summary>The checked connection, or <see cref="Stream.Null"/> after a refusal was sent.</summary>
    private async Task<Stream> ConnectAsync(Stream client, string host, int port, CancellationToken ct)
    {
        try
        {
            return await _connect(host, port, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException)
        {
            Log.Warning("Browser connection blocked: {Host}:{Port} ({Reason})", host, port, ex.Message);
            await ReplyAsync(client, "403 Forbidden", ct);
            return Stream.Null;
        }
    }

    internal static bool TrySplitHostPort(string text, int defaultPort, out string host, out int port)
    {
        port = defaultPort;
        host = text;
        if (text.StartsWith('['))
        {
            var close = text.IndexOf(']', StringComparison.Ordinal);
            if (close < 0)
            {
                return false;
            }
            host = text[1..close];
            var rest = text[(close + 1)..];
            return rest.Length == 0 || rest[0] == ':' && int.TryParse(rest[1..], out port) && port is > 0 and < 65536;
        }
        var colon = text.LastIndexOf(':');
        if (colon >= 0)
        {
            host = text[..colon];
            if (!int.TryParse(text[(colon + 1)..], out port) || port is <= 0 or >= 65536)
            {
                return false;
            }
        }
        return host.Length > 0;
    }

    private static async Task<string?> ReadHeadAsync(NetworkStream stream, CancellationToken ct)
    {
        // Byte by byte up to the blank line, so no body bytes are read past the head.
        var buffer = new List<byte>(1024);
        var one = new byte[1];
        while (buffer.Count < MaxHeaderBytes)
        {
            if (await stream.ReadAsync(one, ct) == 0)
            {
                return null;
            }
            buffer.Add(one[0]);
            var n = buffer.Count;
            if (n >= 4 && buffer[n - 4] == '\r' && buffer[n - 3] == '\n' && buffer[n - 2] == '\r' && buffer[n - 1] == '\n')
            {
                return Encoding.ASCII.GetString(buffer.ToArray(), 0, n - 4);
            }
        }
        return null;
    }

    internal const string BlockedText = "Starlight blocked this address: it is not on the public web.";

    private static Task ReplyAsync(Stream stream, string status, CancellationToken ct)
    {
        var body = status.StartsWith("403", StringComparison.Ordinal) ? BlockedText : "";
        return stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: text/plain\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"), ct).AsTask();
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
        }
        _stop.Dispose();
    }
}
