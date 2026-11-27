using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Starlight.Tools.Web;

public sealed record FetchedPage(Uri Url, int Status, string ContentType, string Body);

/// <summary>Plain HTTP for the web tools: public addresses only, the polite limiter, a size cap, an honest agent name.</summary>
public sealed class WebHttp : IDisposable
{
    public const int MaxBytes = 4_000_000;
    public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Safari/537.36 Starlight/1.0";
    private readonly HttpClient _client;
    private readonly PoliteLimiter _limiter;

    /// <param name="publicOnly">False only for addresses the user typed in Settings (their own SearXNG).</param>
    public WebHttp(PoliteLimiter limiter, HttpMessageHandler? handler = null, bool publicOnly = true, TimeSpan? timeout = null)
    {
        _limiter = limiter;
        handler ??= new SocketsHttpHandler
        {
            ConnectCallback = publicOnly ? UrlGuard.ConnectPublicAsync : null,
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 6,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            UseCookies = false,
        };
        _client = new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromSeconds(20) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/json;q=0.9,text/plain;q=0.8,*/*;q=0.5");
        _client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en;q=0.9,*;q=0.5");
    }

    public async Task<FetchedPage> GetAsync(Uri url, CancellationToken ct, IEnumerable<(string Name, string Value)>? headers = null, HttpContent? post = null)
    {
        await _limiter.WaitAsync(url, ct);
        using var request = new HttpRequestMessage(post is null ? HttpMethod.Get : HttpMethod.Post, url) { Content = post };
        foreach (var (n, v) in headers ?? [])
        {
            request.Headers.TryAddWithoutValidation(n, v);
        }
        HttpResponseMessage sent;
        try
        {
            sent = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // HttpClient's own timeout, not the user: say so instead of "Cancelled".
            throw new HttpRequestException($"{url.Host} did not answer within {_client.Timeout.TotalSeconds:0} seconds.", ex);
        }
        using var response = sent;
        var type = response.Content.Headers.ContentType?.MediaType ?? "";
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxBytes)
            {
                break;
            }
        }
        var body = Decode(buffer.ToArray(), response.Content.Headers.ContentType);
        return new FetchedPage(response.RequestMessage?.RequestUri ?? url, (int)response.StatusCode, type, body);
    }

    static WebHttp() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); // windows-1252, Shift_JIS and others

    internal static string Decode(byte[] bytes, MediaTypeHeaderValue? type)
    {
        // The header first, then a byte order mark, then <meta charset> in the first bytes of the page.
        var name = type?.CharSet?.Trim('"');
        if (string.IsNullOrEmpty(name))
        {
            if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble))
            {
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            }
            var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 2048));
            var m = System.Text.RegularExpressions.Regex.Match(head, @"<meta[^>]+charset\s*=\s*[""']?([A-Za-z0-9_\-:.]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            name = m.Success ? m.Groups[1].Value : null;
        }
        if (!string.IsNullOrEmpty(name))
        {
            try
            {
                return Encoding.GetEncoding(name).GetString(bytes);
            }
            catch (ArgumentException)
            {
            }
        }
        return Encoding.UTF8.GetString(bytes);
    }

    public void Dispose() => _client.Dispose();
}
