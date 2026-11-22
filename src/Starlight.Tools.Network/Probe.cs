// The checks only read status codes and certificate dates and send nothing secret, so any certificate is
// accepted for the connection; the TLS check reports whether Windows trusts it (CA5359).
#pragma warning disable CA5359
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;

namespace Starlight.Tools.Network;

public sealed record ProbeResult(bool Ok, string Text);

/// <summary>The network checks behind server_check and the watcher. Tests use a fake.</summary>
public interface INetProbe
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct);
    Task<ProbeResult> PingAsync(string host, CancellationToken ct);
    Task<ProbeResult> PortAsync(string host, int port, CancellationToken ct);
    Task<ProbeResult> HttpAsync(Uri url, CancellationToken ct);
    /// <summary>Days until the TLS certificate ends, or null if there is no TLS there.</summary>
    Task<(int? DaysLeft, string Text)> TlsAsync(string host, int port, CancellationToken ct);
}

public sealed class NetProbe : INetProbe, IDisposable
{
    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        // Self-signed certificates are common at home; the TLS check reports them, HTTP still answers.
        SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, _, _, _) => true },
        AllowAutoRedirect = true,
    }) { Timeout = TimeSpan.FromSeconds(10) };

    public async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) =>
        IPAddress.TryParse(host, out var ip) ? [ip] : await Dns.GetHostAddressesAsync(host, ct);

    public async Task<ProbeResult> PingAsync(string host, CancellationToken ct)
    {
        using var ping = new Ping();
        try
        {
            var reply = await ping.SendPingAsync(host, TimeSpan.FromSeconds(2), cancellationToken: ct);
            return reply.Status == IPStatus.Success
                ? new ProbeResult(true, $"ping {reply.RoundtripTime} ms")
                : new ProbeResult(false, $"ping: {reply.Status}");
        }
        catch (PingException ex)
        {
            return new ProbeResult(false, "ping failed: " + (ex.InnerException?.Message ?? ex.Message));
        }
    }

    public async Task<ProbeResult> PortAsync(string host, int port, CancellationToken ct)
    {
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var sw = Stopwatch.StartNew();
        try
        {
            await client.ConnectAsync(host, port, timeout.Token);
            return new ProbeResult(true, $"port {port} open ({sw.ElapsedMilliseconds} ms)");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ProbeResult(false, $"port {port}: no answer in 3 s");
        }
        catch (SocketException ex)
        {
            return new ProbeResult(false, $"port {port} closed ({ex.SocketErrorCode})");
        }
    }

    public async Task<ProbeResult> HttpAsync(Uri url, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            var code = (int)response.StatusCode;
            return new ProbeResult(code < 400, $"HTTP {code} {response.ReasonPhrase} ({sw.ElapsedMilliseconds} ms)");
        }
        catch (HttpRequestException ex)
        {
            return new ProbeResult(false, "HTTP failed: " + ex.Message);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ProbeResult(false, "HTTP: no answer in 10 s");
        }
    }

    public async Task<(int? DaysLeft, string Text)> TlsAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(host, port, timeout.Token);
            var valid = true;
            await using var ssl = new SslStream(client.GetStream(), false, (_, _, _, errors) => { valid = errors == SslPolicyErrors.None; return true; });
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, timeout.Token);
            if (ssl.RemoteCertificate is not System.Security.Cryptography.X509Certificates.X509Certificate cert)
            {
                return (null, "no certificate");
            }
            var end = DateTime.Parse(cert.GetExpirationDateString(), System.Globalization.CultureInfo.CurrentCulture);
            var days = (int)Math.Floor((end - DateTime.Now).TotalDays);
            return (days, $"certificate ends {end:d MMM yyyy} ({days} days){(valid ? "" : ", not trusted by Windows")}");
        }
        catch (Exception ex) when (ex is SocketException or IOException or System.Security.Authentication.AuthenticationException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return (null, "no TLS: " + ex.Message);
        }
    }

    public void Dispose() => _http.Dispose();
}
