using System.Net;
using System.Text;
using Starlight.Security;
using Starlight.Settings;
using Starlight.Tools;
using Starlight.Tools.Web;

namespace Starlight.Tests;

internal sealed class FakeClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Answers requests from a table of address to (status, type, body), and records what was asked.</summary>
internal sealed class FakeWeb : HttpMessageHandler
{
    public Dictionary<string, (int Status, string Type, string Body)> Pages { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Asked { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.ToString();
        Asked.Add(url);
        var (status, type, body) = Pages.TryGetValue(url, out var p) ? p : (404, "text/html", "not found");
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, type), RequestMessage = request };
        return Task.FromResult(response);
    }
}

internal sealed class FakeBrowser : IBrowser
{
    public List<string> States { get; } = [];
    public List<string> Scripts { get; } = [];
    public List<BrowserCookie> Cookies { get; } = [];
    public List<(Uri Url, int Cookies)> Rendered { get; } = [];
    public string Html { get; set; } = "<html><body><main><p>Rendered by the browser with enough words to count as a page.</p></main></body></html>";

    public Task<(Uri Url, string Html)> RenderAsync(Uri url, IReadOnlyList<BrowserCookie> cookies, CancellationToken ct)
    {
        Rendered.Add((url, cookies.Count));
        return Task.FromResult((url, Html));
    }

    public Task<IBrowserSession> OpenAsync(Uri url, IReadOnlyList<BrowserCookie> cookies, CancellationToken ct) =>
        Task.FromResult<IBrowserSession>(new Session(this, url));

    private sealed class Session(FakeBrowser b, Uri url) : IBrowserSession
    {
        public Uri Url => url;

        public Task<string> EvaluateAsync(string script, CancellationToken ct)
        {
            b.Scripts.Add(script);
            if (script.Contains("return 'ok'", StringComparison.Ordinal))
            {
                var s = b.States[0];
                b.States.RemoveAt(0);
                return Task.FromResult(s);
            }
            return Task.FromResult(script.Contains("'user-only'", StringComparison.Ordinal) ? "submitted" : "submitted");
        }

        public Task WaitForLoadAsync(TimeSpan max, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<BrowserCookie>> CookiesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<BrowserCookie>>(b.Cookies);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class FixedPrompt(string? answer) : IUserPrompt
{
    public int Asked { get; private set; }

    public Task<string?> AskTextAsync(string title, string question, CancellationToken ct)
    {
        Asked++;
        return Task.FromResult(answer);
    }
}

public sealed class WebTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Limiter_allows_one_request_per_second_per_site()
    {
        var clock = new FakeClock(T0);
        var limiter = new PoliteLimiter(() => 5, clock);
        Assert.Equal(TimeSpan.Zero, limiter.Reserve("a.com"));
        Assert.Equal(TimeSpan.FromSeconds(1), limiter.Reserve("a.com"));
        Assert.Equal(TimeSpan.FromSeconds(2), limiter.Reserve("a.com"));
        clock.Now = T0.AddSeconds(5);
        Assert.Equal(TimeSpan.Zero, limiter.Reserve("a.com"));
    }

    [Fact]
    public void Limiter_spaces_all_requests_to_the_overall_rate()
    {
        var limiter = new PoliteLimiter(() => 5, new FakeClock(T0));
        var waits = Enumerable.Range(0, 10).Select(i => limiter.Reserve($"site{i}.com")).ToList();
        Assert.Equal(TimeSpan.Zero, waits[0]);
        Assert.Equal(TimeSpan.FromMilliseconds(200), waits[1]);
        Assert.Equal(TimeSpan.FromMilliseconds(1800), waits[9]);
        // Never more than 5 in any second.
        Assert.True(waits.Count(w => w < TimeSpan.FromSeconds(1)) <= 5);
    }

    [Fact]
    public void Limiter_fills_a_gap_left_by_a_site_waiting_its_turn()
    {
        var limiter = new PoliteLimiter(() => 5, new FakeClock(T0));
        limiter.Reserve("a.com");
        Assert.Equal(TimeSpan.FromSeconds(1), limiter.Reserve("a.com"));
        Assert.Equal(TimeSpan.FromMilliseconds(200), limiter.Reserve("b.com"));
    }

    [Fact]
    public void Robots_uses_the_starlight_group_else_star_and_the_longest_rule()
    {
        var rules = RobotsRules.Parse("""
            User-agent: *
            Disallow: /private
            Allow: /private/open
            Disallow: /*.pdf$

            User-agent: OtherBot
            Disallow: /
            """);
        Assert.True(rules.Allows("/"));
        Assert.False(rules.Allows("/private/x"));
        Assert.True(rules.Allows("/private/open/page"));
        Assert.False(rules.Allows("/docs/file.pdf"));
        Assert.True(rules.Allows("/docs/file.pdf?x=1"));

        var mine = RobotsRules.Parse("User-agent: *\nDisallow: /\n\nUser-agent: Starlight\nDisallow: /admin\n");
        Assert.True(mine.Allows("/page"));
        Assert.False(mine.Allows("/admin/x"));
    }

    [Fact]
    public void Page_text_keeps_the_main_content_and_drops_menus_and_scripts()
    {
        var page = PageText.Clean("""
            <html><head><title>Kettle guide</title><script>var x=1;</script></head><body>
            <nav><a href="/">Home</a><a href="/shop">Shop</a></nav>
            <main><h1>How to descale a kettle</h1><p>Use   white vinegar
            and water.</p><ul><li>Fill half</li><li>Boil</li></ul><div aria-hidden="true">hidden</div></main>
            <footer>Copyright</footer></body></html>
            """);
        Assert.Equal("Kettle guide", page.Title);
        Assert.Contains("# How to descale a kettle", page.Text);
        Assert.Contains("Use white vinegar and water.", page.Text);
        Assert.Contains("- Fill half", page.Text);
        Assert.DoesNotContain("Shop", page.Text);
        Assert.DoesNotContain("Copyright", page.Text);
        Assert.DoesNotContain("var x", page.Text);
        Assert.DoesNotContain("hidden", page.Text);
        Assert.False(page.NeedsScripts);
    }

    [Fact]
    public void An_empty_app_shell_needs_the_browser()
    {
        var page = PageText.Clean("<html><body><div id=\"root\"></div><script src=a.js></script><script src=b.js></script></body></html>");
        Assert.True(page.NeedsScripts);
    }

    [Fact]
    public void Parses_duckduckgo_brave_and_searxng_results()
    {
        var ddg = SearchEngines.ParseDuckDuckGo("""
            <div class="result results_links result--ad"><a class="result__a" href="https://duckduckgo.com/y.js?ad=1">Ad</a></div>
            <div class="result results_links"><h2><a class="result__a" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.com%2Fa%3Fb%3D1&amp;rut=x">Example <b>A</b></a></h2>
            <a class="result__snippet">The first   result.</a></div>
            """);
        var hit = Assert.Single(ddg);
        Assert.Equal("https://example.com/a?b=1", hit.Url);
        Assert.Equal("Example A", hit.Title);
        Assert.Equal("The first result.", hit.Snippet);

        var brave = SearchEngines.ParseBrave("""{"web":{"results":[{"title":"B <strong>one</strong>","url":"https://b.com","description":"x &amp; <strong>y</strong>"}]}}""");
        Assert.Equal(new SearchHit("B one", "https://b.com", "x & y"), Assert.Single(brave));

        var sx = SearchEngines.ParseSearxng("""{"results":[{"title":"S","url":"https://s.com","content":"c"},{"title":"no url"}]}""");
        Assert.Equal("https://s.com", Assert.Single(sx).Url);
    }

    [Fact]
    public void Search_engine_choice_follows_settings_and_what_is_set_up()
    {
        Assert.Equal("duckduckgo", SearchEngines.Choose("auto", "", false));
        Assert.Equal("brave", SearchEngines.Choose("auto", "", true));
        Assert.Equal("searxng", SearchEngines.Choose("auto", "http://nas:8080", true));
        Assert.Equal("brave", SearchEngines.Choose("brave", "http://nas:8080", true));
        Assert.Equal("searxng", SearchEngines.Choose("brave", "http://nas:8080", false));
        Assert.Equal("duckduckgo", SearchEngines.Choose("duckduckgo", "http://nas:8080", true));
    }

    [Theory]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("ftp://example.com")]
    [InlineData("http://localhost:8080")]
    [InlineData("http://127.0.0.1")]
    [InlineData("http://192.168.1.1/admin")]
    [InlineData("10.0.0.5")]
    [InlineData("http://[::1]/")]
    [InlineData("http://printer.local")]
    public void Web_tools_refuse_local_and_non_web_addresses(string url) =>
        Assert.Throws<ToolException>(() => UrlGuard.Parse(url));

    [Fact]
    public void Web_addresses_get_https_when_typed_without_it()
    {
        Assert.Equal("https://example.com/a", UrlGuard.Parse("example.com/a").ToString());
        Assert.False(UrlGuard.IsPublic(IPAddress.Parse("100.64.1.1")));
        Assert.False(UrlGuard.IsPublic(IPAddress.Parse("fd00::1")));
        Assert.True(UrlGuard.IsPublic(IPAddress.Parse("93.184.216.34")));
    }

    [Fact]
    public void Sessions_are_kept_per_site_and_drop_expired_and_foreign_cookies()
    {
        using var env = new TestEnv();
        var vault = new SessionVault(Path.Combine(env.Root, "sessions"), b => b.Reverse().ToArray(), b => b.Reverse().ToArray());
        Assert.Equal("example.com", SessionVault.SiteOf("mail.example.com"));
        Assert.Equal("bbc.co.uk", SessionVault.SiteOf("www.bbc.co.uk"));
        var future = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds();
        vault.Save("www.example.com", [
            new BrowserCookie("sid", "1", ".example.com", "/", future, true, true, "Lax"),
            new BrowserCookie("old", "2", "example.com", "/", 1000, false, true, null),
            new BrowserCookie("ads", "3", ".tracker.net", "/", future, false, true, null),
        ]);
        var loaded = vault.Load("app.example.com");
        Assert.Equal("sid", Assert.Single(loaded).Name);
        Assert.Empty(vault.Load("other.org"));
        // What is on disk went through the protector (DPAPI in the app).
        var onDisk = File.ReadAllBytes(Directory.GetFiles(Path.Combine(env.Root, "sessions"))[0]);
        Assert.StartsWith("[", Encoding.UTF8.GetString(onDisk.Reverse().ToArray()), StringComparison.Ordinal);
        Assert.False(Encoding.UTF8.GetString(onDisk).StartsWith('['));
    }

    [Fact]
    public void Excerpt_keeps_the_paragraphs_about_the_question()
    {
        var filler = string.Join('\n', Enumerable.Range(0, 40).Select(i => $"Paragraph {i} talks about gardening and other unrelated matters at length."));
        var text = filler + "\nThe Eiffel Tower is 330 metres tall since the 2022 antenna was added.\n" + filler;
        var excerpt = Excerpt.Best(text, "How tall is the Eiffel Tower?", 400);
        Assert.Contains("330 metres", excerpt);
        Assert.True(excerpt.Length <= 420);
    }

    private sealed class Rig : IDisposable
    {
        public Rig()
        {
            Env = new TestEnv();
            Limiter = new PoliteLimiter(() => 1000);
            Http = new WebHttp(Limiter, Web);
            Vault = new SessionVault(Path.Combine(Env.Root, "sessions"), b => b, b => b);
            Tools = new WebTools(Env.Settings, Secrets, Http, Http, Browser, Vault, Prompt);
            Env.Registry.Add(Tools);
        }

        public TestEnv Env { get; }
        public FakeWeb Web { get; } = new();
        public FakeBrowser Browser { get; } = new();
        public MemorySecretStore Secrets { get; } = new();
        public FixedPrompt Prompt { get; set; } = new("123456");
        public PoliteLimiter Limiter { get; }
        public WebHttp Http { get; }
        public SessionVault Vault { get; }
        public WebTools Tools { get; }

        public Task<ToolResult> Call(string tool, params (string, object?)[] args) =>
            Env.Dispatcher(new FixedApprovals(ApprovalAnswer.Yes)).InvokeAsync(tool, args.ToDictionary(a => a.Item1, a => a.Item2), "t1", "chat", default);

        public void Dispose()
        {
            Http.Dispose();
            Env.Dispose();
        }
    }

    private const string Article = "<html><head><title>Tea</title></head><body><main><h1>Green tea</h1><p>Green tea is brewed at 80 degrees for two minutes, which keeps it from turning bitter.</p></main></body></html>";

    [Fact]
    public async Task Web_read_returns_the_page_text_marked_as_outside_content()
    {
        using var rig = new Rig();
        rig.Web.Pages["https://tea.example/green"] = (200, "text/html", Article);

        var r = await rig.Call("web_read", ("url", "tea.example/green"));

        Assert.True(r.Ok, r.Text);
        Assert.StartsWith(ToolDispatcher.UntrustedMark, r.Text);
        Assert.Contains("80 degrees", r.Text);
        Assert.Empty(rig.Browser.Rendered);
    }

    [Fact]
    public async Task Web_read_uses_the_browser_for_script_pages_and_for_saved_logins()
    {
        using var rig = new Rig();
        rig.Web.Pages["https://app.example/"] = (200, "text/html", "<html><body><div id=app></div><script></script><script></script><script></script></body></html>");
        var r = await rig.Call("web_read", ("url", "https://app.example/"));
        Assert.Contains("Rendered by the browser", r.Text);
        Assert.Equal(0, rig.Browser.Rendered[0].Cookies);

        rig.Vault.Save("mail.example.org", [new BrowserCookie("sid", "1", ".example.org", "/", -1, true, true, null)]);
        await rig.Call("web_read", ("url", "https://www.example.org/inbox"));
        Assert.Equal(1, rig.Browser.Rendered[1].Cookies);
        Assert.DoesNotContain(rig.Web.Asked, a => a.Contains("example.org", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Web_search_uses_duckduckgo_when_nothing_else_is_set_up()
    {
        using var rig = new Rig();
        rig.Web.Pages[SearchEngines.DuckDuckGoUri.ToString()] = (200, "text/html",
            """<div class="result"><a class="result__a" href="https://tea.example/green">Green tea</a><a class="result__snippet">How to brew.</a></div>""");

        var r = await rig.Call("web_search", ("query", "brew green tea"));

        Assert.True(r.Ok, r.Text);
        Assert.Contains("1. Green tea", r.Text);
        Assert.Contains("https://tea.example/green", r.Text);
    }

    [Fact]
    public async Task Web_search_tells_the_model_when_brave_refuses_the_key()
    {
        using var rig = new Rig();
        rig.Secrets.Set(WebTools.BraveKey, "", "bad-key");
        rig.Web.Pages[SearchEngines.BraveUri("x").ToString()] = (401, "application/json", "{}");

        var r = await rig.Call("web_search", ("query", "x"));

        Assert.False(r.Ok);
        Assert.Contains("Settings > Web", r.Text);
    }

    [Fact]
    public async Task Research_reads_the_sources_numbers_them_and_respects_robots()
    {
        using var rig = new Rig();
        rig.Web.Pages[SearchEngines.DuckDuckGoUri.ToString()] = (200, "text/html", """
            <div class="result"><a class="result__a" href="https://tea.example/green">Tea one</a><a class="result__snippet">s1</a></div>
            <div class="result"><a class="result__a" href="https://closed.example/page">Closed</a><a class="result__snippet">s2</a></div>
            """);
        rig.Web.Pages["https://tea.example/green"] = (200, "text/html", Article);
        rig.Web.Pages["https://closed.example/robots.txt"] = (200, "text/plain", "User-agent: *\nDisallow: /");
        rig.Web.Pages["https://closed.example/page"] = (200, "text/html", Article);

        var r = await rig.Call("web_research", ("question", "green tea temperature"));

        Assert.True(r.Ok, r.Text);
        Assert.Contains("[1] Tea one - https://tea.example/green", r.Text);
        Assert.Contains("[2] Closed - https://closed.example/page (not read: robots.txt", r.Text);
        Assert.Contains("80 degrees", r.Text);
        Assert.DoesNotContain(rig.Web.Asked, a => a == "https://closed.example/page");
    }

    [Fact]
    public async Task Login_fills_the_stored_password_asks_for_the_code_and_keeps_the_session()
    {
        using var rig = new Rig();
        rig.Env.Settings.Update(s => s.Web.Logins.Add(new WebLogin { Name = "Example", LoginUrl = "https://login.example.com/signin" }));
        rig.Secrets.Set("login/login.example.com", "me@example.com", "s3cret\"pw");
        rig.Browser.States.AddRange(["code", "ok"]);
        rig.Browser.Cookies.Add(new BrowserCookie("sid", "1", ".example.com", "/", -1, true, true, null));

        var r = await rig.Call("web_login", ("site", "example"));

        Assert.True(r.Ok, r.Text);
        Assert.Contains("Signed in to Example", r.Text);
        Assert.Equal(1, rig.Prompt.Asked);
        Assert.Contains(rig.Browser.Scripts, s => s.Contains("\"s3cret\\u0022pw\"", StringComparison.Ordinal));
        Assert.Contains(rig.Browser.Scripts, s => s.Contains("\"123456\"", StringComparison.Ordinal));
        Assert.Single(rig.Vault.Load("www.example.com"));
        // The password never reaches the model or the audit.
        Assert.DoesNotContain("s3cret", r.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(rig.Env.Db.RecentAudit(10), a => a.ArgumentsJson.Contains("s3cret", StringComparison.Ordinal) || a.Result.Contains("s3cret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Login_reports_a_wrong_password_and_saves_nothing()
    {
        using var rig = new Rig();
        rig.Env.Settings.Update(s => s.Web.Logins.Add(new WebLogin { Name = "Example", LoginUrl = "https://example.com/login" }));
        rig.Secrets.Set("login/example.com", "me", "wrong");
        rig.Browser.States.AddRange(["password", "password", "password"]);

        var r = await rig.Call("web_login", ("site", "Example"));

        Assert.Contains("did not work", r.Text);
        Assert.False(rig.Vault.Has("example.com"));
    }

    [Fact]
    public async Task Login_without_a_set_up_site_points_to_settings()
    {
        using var rig = new Rig();
        var r = await rig.Call("web_login", ("site", "github"));
        Assert.Contains("Settings > Web", r.Text);
    }

    [Fact]
    public async Task A_refused_password_is_sent_only_once()
    {
        using var rig = new Rig();
        rig.Env.Settings.Update(s => s.Web.Logins.Add(new WebLogin { Name = "Example", LoginUrl = "https://example.com/login" }));
        rig.Secrets.Set("login/example.com", "me", "wrongpw");
        rig.Browser.States.AddRange(["password", "password", "password"]);

        await rig.Call("web_login", ("site", "Example"));

        Assert.Single(rig.Browser.Scripts, s => s.Contains("wrongpw", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_duckduckgo_bot_check_is_not_reported_as_no_results()
    {
        using var rig = new Rig();
        rig.Web.Pages[SearchEngines.DuckDuckGoUri.ToString()] = (202, "text/html", "<html><body><div class=anomaly-modal>Unfortunately, bots use DuckDuckGo too.</div></body></html>");

        var r = await rig.Call("web_search", ("query", "green tea"));

        Assert.False(r.Ok);
        Assert.Contains("blocked", r.Text);
        Assert.False(SearchEngines.IsDuckDuckGoBlock(200, "<div class=\"no-results\">No results.</div>"));
    }

    private sealed class SlowWeb : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task A_slow_site_is_reported_as_a_timeout_not_as_cancelled()
    {
        using var http = new WebHttp(new PoliteLimiter(() => 1000), new SlowWeb(), timeout: TimeSpan.FromMilliseconds(200));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync(new Uri("https://slow.example/"), default));
        Assert.Contains("did not answer", ex.Message);
    }
}

public sealed class PageTextFixTests
{
    [Fact]
    public void Code_in_pre_blocks_keeps_its_indentation()
    {
        var html = "<html><body><main><p>An example:</p><pre>def f():\n    if x:\n        return 1</pre></main></body></html>";
        var text = PageText.Clean(html).Text;
        Assert.Contains("def f():\n    if x:\n        return 1", text);
        Assert.False(text.Contains('\u0001', StringComparison.Ordinal));
    }

    [Fact]
    public void Very_deep_html_does_not_overflow()
    {
        var html = "<html><body><main>" + string.Concat(Enumerable.Repeat("<div>", 20000)) + "deep words here" + "</main></body></html>";
        Assert.Contains("deep words here", PageText.Clean(html).Text);
    }

    [Fact]
    public void A_page_wrapped_in_a_form_keeps_its_text_and_a_search_form_goes()
    {
        var page = "<html><body><form id=aspnetForm><div><p>First paragraph of the article.</p><p>Second one.</p><p>Third one.</p></div></form></body></html>";
        Assert.Contains("Second one.", PageText.Clean(page).Text);
        var search = "<html><body><main><form><input name=q><p>Search the site</p></form><p>Real text.</p></main></body></html>";
        Assert.DoesNotContain("Search the site", PageText.Clean(search).Text);
    }

    [Fact]
    public void Inline_parts_of_one_word_stay_together()
    {
        var text = PageText.Clean("<html><body><main><p><b>Star</b>light is <i>here</i>.</p></main></body></html>").Text;
        Assert.Equal("Starlight is here.", text);
    }
}

public sealed class WebHttpFixTests
{
    [Theory]
    [InlineData("www.bbc.de", "bbc.de")]
    [InlineData("mail.gmx.at", "gmx.at")]
    [InlineData("shop.example.com.au", "example.com.au")]
    [InlineData("alice.github.io", "alice.github.io")]
    [InlineData("www.bob.vercel.app", "bob.vercel.app")]
    [InlineData("1.2.3.4", "1.2.3.4")]
    public void Site_of_a_country_domain(string host, string site) => Assert.Equal(site, SessionVault.SiteOf(host));

    [Fact]
    public void Meta_charset_and_legacy_encodings_are_read()
    {
        var latin = System.Text.Encoding.Latin1.GetBytes("<html><head><meta charset=\"windows-1252\"></head><body>Café über</body></html>");
        Assert.Contains("Café über", WebHttp.Decode(latin, null));
        var header = new System.Net.Http.Headers.MediaTypeHeaderValue("text/html") { CharSet = "iso-8859-1" };
        Assert.Contains("über", WebHttp.Decode(latin, header));
    }
}

public sealed class WebReadAskTests
{
    [Fact]
    public async Task Web_read_asks_once_the_turn_has_read_outside_content()
    {
        using var env = new TestEnv();
        var web = new FakeWeb();
        web.Pages["https://tea.example/a"] = (200, "text/html", "<html><body><main><p>A page about tea with enough words to be read as a page.</p></main></body></html>");
        web.Pages["https://tea.example/b"] = web.Pages["https://tea.example/a"];
        using var http = new WebHttp(new PoliteLimiter(() => 1000), web);
        env.Registry.Add(new WebTools(env.Settings, new Starlight.Security.MemorySecretStore(), http, http, new FakeBrowser(),
            new SessionVault(Path.Combine(env.Root, "s"), b => b, b => b), new FixedPrompt(null)));
        var approvals = new FixedApprovals(ApprovalAnswer.Yes);
        var dispatcher = env.Dispatcher(approvals);

        await dispatcher.InvokeAsync("web_read", new Dictionary<string, object?> { ["url"] = "https://tea.example/a" }, "t1", "chat", default);
        Assert.Equal(0, approvals.Asked);
        await dispatcher.InvokeAsync("web_read", new Dictionary<string, object?> { ["url"] = "https://tea.example/b" }, "t1", "chat", default);
        Assert.Equal(1, approvals.Asked);
    }

    [Fact]
    public void Search_and_research_also_ask_after_outside_content()
    {
        using var env = new TestEnv();
        using var http = new WebHttp(new PoliteLimiter(() => 1000), new FakeWeb());
        env.Registry.Add(new WebTools(env.Settings, new Starlight.Security.MemorySecretStore(), http, http, new FakeBrowser(),
            new SessionVault(Path.Combine(env.Root, "s"), b => b, b => b), new FixedPrompt(null)));
        Assert.True(env.Registry.Find("web_search")!.AskAfterUntrusted);
        Assert.True(env.Registry.Find("web_research")!.AskAfterUntrusted);
    }
}
