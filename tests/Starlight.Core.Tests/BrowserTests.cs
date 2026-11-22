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

    private static EdgeBrowser Browser(TestEnv env) =>
        new(Path.Combine(env.Root, "profile"), Chromium) { ExtraArgs = ["--no-sandbox", "--disable-gpu"] };

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
