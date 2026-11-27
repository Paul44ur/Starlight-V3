using System.Net;
using System.Net.Sockets;
using System.Text;
using Starlight.Tools.Web;

namespace Starlight.Tests;

/// <summary>
/// The DevTools driver and the login scripts against a real Chromium (the container has one; on Windows the
/// app uses Edge, which speaks the same protocol). Pages come from a local server.
/// </summary>
public sealed class BrowserTests
{
    private const string Chromium = "/opt/pw-browsers/chromium";
    private static bool CanRun => !OperatingSystem.IsWindows() && File.Exists(Chromium);

    private sealed class Site : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _loop;

        public Site(Func<HttpListenerRequest, string, (string Body, string? Cookie)> handle)
        {
            using (var l = new TcpListener(IPAddress.Loopback, 0))
            {
                l.Start();
                Port = ((IPEndPoint)l.LocalEndpoint).Port;
            }
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
            _loop = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try
                    {
                        ctx = await _listener.GetContextAsync();
                    }
                    catch (Exception)
                    {
                        return;
                    }
                    using var reader = new StreamReader(ctx.Request.InputStream);
                    var form = await reader.ReadToEndAsync();
                    var (body, cookie) = handle(ctx.Request, form);
                    if (cookie is not null)
                    {
                        ctx.Response.Headers.Add("Set-Cookie", cookie);
                    }
                    if (body.StartsWith("redirect:", StringComparison.Ordinal))
                    {
                        ctx.Response.StatusCode = 302;
                        ctx.Response.Headers.Add("Location", body["redirect:".Length..]);
                        ctx.Response.Close();
                        continue;
                    }
                    ctx.Response.ContentType = "text/html; charset=utf-8";
                    var bytes = Encoding.UTF8.GetBytes(body);
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                    ctx.Response.Close();
                }
            });
        }

        public int Port { get; }
        public Uri Url(string path) => new($"http://127.0.0.1:{Port}{path}");

        public void Dispose()
        {
            _listener.Stop();
            _listener.Close();
        }
    }

    // The test site is on 127.0.0.1, so it stands in for the public web here; "/private" stands in for the home network.
    private static EdgeBrowser Browser(TestEnv env) =>
        new(Path.Combine(env.Root, "profile"), Chromium)
        {
            ExtraArgs = ["--no-sandbox", "--disable-gpu"],
            Allow = (u, _) => Task.FromResult(!u.AbsolutePath.StartsWith("/private", StringComparison.Ordinal)),
            Connect = PlainConnectAsync,
        };

    internal static async ValueTask<Stream> PlainConnectAsync(string host, int port, CancellationToken ct)
    {
        var c = new TcpClient();
        await c.ConnectAsync(host, port, ct);
        return c.GetStream();
    }

    [Fact]
    public async Task A_local_address_is_refused_at_the_connection_even_when_the_first_check_passed()
    {
        // Stands in for DNS rebinding: the request check says yes, but the address the browser would reach is local.
        Assert.SkipUnless(CanRun, "needs Chromium on Linux");
        using var env = new TestEnv();
        var hits = 0;
        using var site = new Site((_, _) => { Interlocked.Increment(ref hits); return ("<html><body>router admin</body></html>", null); });
        await using var browser = new EdgeBrowser(Path.Combine(env.Root, "profile"), Chromium)
        {
            ExtraArgs = ["--no-sandbox", "--disable-gpu"],
            Allow = (_, _) => Task.FromResult(true),
        };

        try
        {
            var (_, html) = await browser.RenderAsync(site.Url("/"), [], default);
            Assert.DoesNotContain("router admin", html);
        }
        catch (Starlight.Tools.ToolException)
        {
        }
        Assert.Equal(0, hits);
    }

    [Fact]
    public async Task Redirects_and_images_to_blocked_addresses_never_load()
    {
        Assert.SkipUnless(CanRun, "needs Chromium on Linux");
        using var env = new TestEnv();
        var hits = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var site = new Site((req, _) =>
        {
            hits.Add(req.Url!.AbsolutePath);
            return req.Url.AbsolutePath switch
            {
                "/page" => ("<html><body><main><p>A public page with a picture.</p><img src=/private/pixel.png></main></body></html>", null),
                "/go" => ("redirect:/private/admin", null),
                _ => ("<html><body>router admin</body></html>", null),
            };
        });
        await using var browser = Browser(env);

        var (_, html) = await browser.RenderAsync(site.Url("/page"), [], default);
        Assert.Contains("A public page", html);
        var go = await Assert.ThrowsAsync<Starlight.Tools.ToolException>(() => browser.RenderAsync(site.Url("/go"), [], default));
        Assert.Contains("local network", go.Message);

        Assert.DoesNotContain(hits, h => h.StartsWith("/private", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Public_check_refuses_local_hosts_and_odd_schemes()
    {
        Assert.False(await UrlGuard.IsPublicAsync(new Uri("http://192.168.1.1/"), default));
        Assert.False(await UrlGuard.IsPublicAsync(new Uri("http://localhost:8080/"), default));
        Assert.False(await UrlGuard.IsPublicAsync(new Uri("http://[::1]/"), default));
        Assert.False(await UrlGuard.IsPublicAsync(new Uri("file:///C:/Windows/win.ini"), default));
        Assert.False(await UrlGuard.IsPublicAsync(new Uri("http://printer.local/"), default));
        Assert.True(await UrlGuard.IsPublicAsync(new Uri("https://93.184.215.14/"), default));
        Assert.True(await UrlGuard.IsPublicAsync(new Uri("data:text/plain,hi"), default));
    }

    [Fact]
    public async Task Renders_a_page_whose_text_comes_from_scripts()
    {
        Assert.SkipUnless(CanRun, "needs Chromium on Linux");
        using var env = new TestEnv();
        using var site = new Site((_, _) => ("<html><body><div id=app></div><script>document.getElementById('app').innerHTML='<main><p>Filled by a script, long enough to read.</p></main>'</script></body></html>", null));
        await using var browser = Browser(env);

        var (url, html) = await browser.RenderAsync(site.Url("/"), [], default);

        Assert.Equal(site.Port, url.Port);
        Assert.Contains("Filled by a script", PageText.Clean(html).Text);
    }

    [Fact]
    public async Task Login_scripts_fill_the_form_detect_the_code_step_and_keep_the_cookie()
    {
        Assert.SkipUnless(CanRun, "needs Chromium on Linux");
        using var env = new TestEnv();
        string? posted = null;
        using var site = new Site((req, form) => req.Url!.AbsolutePath switch
        {
            "/login" when req.HttpMethod == "POST" => (Capture(form, ref posted), null),
            "/login" => ("<html><body><form method=post action=/login><input type=email name=email><input type=password name=pw><button type=submit>Sign in</button></form></body></html>", null),
            "/code" => ("<html><body>Welcome</body></html>", "sid=abc123; Path=/; HttpOnly"),
            _ => ("<html><body>?</body></html>", null),
        });
        await using var browser = Browser(env);
        await using var page = await browser.OpenAsync(site.Url("/login"), [], default);

        Assert.Equal("submitted", await page.EvaluateAsync(LoginScripts.Fill("me@example.com", "pa\"ss'w<rd"), default));
        await page.WaitForLoadAsync(TimeSpan.FromSeconds(10), default);
        await Task.Delay(300);
        Assert.Equal("code", await page.EvaluateAsync(LoginScripts.State, default));
        Assert.Contains("email=me%40example.com", posted);
        Assert.Contains("pw=pa%22ss%27w%3Crd", posted);

        Assert.Equal("submitted", await page.EvaluateAsync(LoginScripts.FillCode("424242"), default));
        await page.WaitForLoadAsync(TimeSpan.FromSeconds(10), default);
        await Task.Delay(300);
        Assert.Equal("ok", await page.EvaluateAsync(LoginScripts.State, default));
        var cookies = await page.CookiesAsync(default);
        Assert.Contains(cookies, c => c.Name == "sid" && c.Value == "abc123");
    }

    private static string Capture(string form, ref string? posted)
    {
        posted = form;
        return "<html><body><form method=get action=/code><input name=otp autocomplete=one-time-code><button>Verify</button></form></body></html>";
    }
}

public sealed class FilterProxyTests
{
    [Theory]
    [InlineData("example.com:443", "example.com", 443)]
    [InlineData("[::1]:8443", "::1", 8443)]
    [InlineData("example.com", "example.com", 443)]
    public void Host_and_port_are_split(string text, string host, int port)
    {
        Assert.True(FilterProxy.TrySplitHostPort(text, 443, out var h, out var p));
        Assert.Equal((host, port), (h, p));
    }

    [Theory]
    [InlineData("example.com:99999")]
    [InlineData("[::1")]
    [InlineData(":443")]
    public void Bad_targets_are_refused(string text) => Assert.False(FilterProxy.TrySplitHostPort(text, 443, out _, out _));

    [Fact]
    public async Task Connect_to_a_local_address_gets_403_and_public_check_is_used()
    {
        await using var proxy = new FilterProxy(UrlGuard.ConnectPublicAsync);
        using var c = new TcpClient();
        await c.ConnectAsync(IPAddress.Loopback, proxy.Port);
        var s = c.GetStream();
        await s.WriteAsync(Encoding.ASCII.GetBytes("CONNECT 127.0.0.1:22 HTTP/1.1\r\nHost: 127.0.0.1:22\r\n\r\n"));
        var reply = await new StreamReader(s).ReadToEndAsync();
        Assert.StartsWith("HTTP/1.1 403", reply);
    }

    [Fact]
    public async Task Plain_http_is_forwarded_to_the_checked_host_in_origin_form()
    {
        using var origin = new TcpListener(IPAddress.Loopback, 0);
        origin.Start();
        var port = ((IPEndPoint)origin.LocalEndpoint).Port;
        var got = Task.Run(async () =>
        {
            using var o = await origin.AcceptTcpClientAsync();
            var os = o.GetStream();
            var buf = new byte[4096];
            var n = await os.ReadAsync(buf);
            await os.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nhi"u8.ToArray());
            return Encoding.ASCII.GetString(buf, 0, n);
        });
        await using var proxy = new FilterProxy(BrowserTests.PlainConnectAsync);
        using var c = new TcpClient();
        await c.ConnectAsync(IPAddress.Loopback, proxy.Port);
        var s = c.GetStream();
        await s.WriteAsync(Encoding.ASCII.GetBytes($"GET http://127.0.0.1:{port}/a?b=1 HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nProxy-Connection: keep-alive\r\n\r\n"));
        var request = await got;
        var reply = await new StreamReader(s).ReadToEndAsync();

        Assert.StartsWith("GET /a?b=1 HTTP/1.1\r\n", request);
        Assert.DoesNotContain("Proxy-Connection", request);
        Assert.Contains("Connection: close", request);
        Assert.EndsWith("hi", reply);
    }
}
