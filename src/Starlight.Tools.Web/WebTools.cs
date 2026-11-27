using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Serilog;
using Starlight.Security;
using Starlight;
using Starlight.Settings;

namespace Starlight.Tools.Web;

/// <summary>The four web tools (plan 5.8). Page text is outside content: the dispatcher marks it and guards Ask tools.</summary>
public sealed class WebTools(
    SettingsStore settings,
    ISecretStore secrets,
    WebHttp http,
    WebHttp localHttp,
    IBrowser browser,
    SessionVault sessions,
    IUserPrompt prompt) : IToolProvider, IAsyncDisposable
{
    /// <summary>The web tools as the app uses them: real HTTP, Edge, sessions in the data folder.</summary>
    public static WebTools Create(StarlightPaths paths, SettingsStore settings, ISecretStore secrets, IUserPrompt prompt)
    {
        var limiter = new PoliteLimiter(() => settings.Current.Web.MaxRequestsPerSecond);
        var browser = new EdgeBrowser(Path.Combine(paths.DataRoot, "browser"));
        return new WebTools(settings, secrets, new WebHttp(limiter), new WebHttp(limiter, publicOnly: false), browser,
            new SessionVault(Path.Combine(paths.DataRoot, "web-sessions")), prompt)
        { _owned = browser };
    }

    private IAsyncDisposable? _owned;

    public async ValueTask DisposeAsync()
    {
        if (_owned is not null)
        {
            await _owned.DisposeAsync();
            http.Dispose();
            localHttp.Dispose();
        }
    }

    public const string BraveKey = "brave";
    private const int ReadChars = 12000;
    private readonly Dictionary<string, (RobotsRules Rules, DateTimeOffset At)> _robots = new(StringComparer.OrdinalIgnoreCase);

    [Tool("web_search", UntrustedOutput = true, TimeoutSeconds = 25)]
    [Description("Searches the web. Returns titles, addresses and short snippets. Use web_read to open a result, or web_research for a question that needs several sources.")]
    public async Task<string> Search([Description("What to search for")] string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ToolException("Give something to search for.");
        }
        var (hits, engine) = await SearchHitsAsync(query, ct);
        return SearchEngines.Format(hits, query, engine);
    }

    [Tool("web_read", UntrustedOutput = true, TimeoutSeconds = 45, MaxResultChars = ReadChars + 400)]
    [Description("Opens a web page and returns its main text. Uses a saved login for the site if there is one.")]
    public async Task<string> Read([Description("The web address, for example https://example.com/page")] string url, CancellationToken ct)
    {
        var page = await ReadPageAsync(UrlGuard.Parse(url), ct);
        var text = page.Text.Length > ReadChars ? page.Text[..ReadChars] + "\n[the page goes on]" : page.Text;
        return $"{page.Title}\n{page.Url}\n\n{(text.Length == 0 ? "(no readable text on this page)" : text)}";
    }

    [Tool("web_research", UntrustedOutput = true, TimeoutSeconds = 120, MaxResultChars = 14000)]
    [Description("Researches a question on the web: searches, reads up to 6 pages and returns the relevant parts with numbered sources. Then answer with a short report that cites the sources as [1], [2].")]
    public async Task<string> Research([Description("The question to research")] string question, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            throw new ToolException("Give the question to research.");
        }
        var (hits, _) = await SearchHitsAsync(question, ct);
        var picked = new List<SearchHit>();
        var perSite = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in hits)
        {
            if (!Uri.TryCreate(h.Url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
            {
                continue;
            }
            var site = SessionVault.SiteOf(u.Host);
            if (perSite.GetValueOrDefault(site) >= 2)
            {
                continue;
            }
            perSite[site] = perSite.GetValueOrDefault(site) + 1;
            picked.Add(h);
            if (picked.Count == 6)
            {
                break;
            }
        }
        if (picked.Count == 0)
        {
            return $"No results for \"{question}\".";
        }
        var reads = await Task.WhenAll(picked.Select(async h =>
        {
            try
            {
                var uri = UrlGuard.Parse(h.Url);
                if (!await RobotsAllowAsync(uri, ct))
                {
                    return new SourceRead(h, null, "robots.txt asks not to read it");
                }
                var page = await ReadPageAsync(uri, ct);
                return new SourceRead(h, page.Text, null);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // One bad source must not end the whole research.
                if (ex is not (ToolException or HttpRequestException or TaskCanceledException))
                {
                    Log.Warning(ex, "Reading a research source failed: {Url}", h.Url);
                }
                return new SourceRead(h, null, ex is TaskCanceledException ? "too slow" : ex.Message);
            }
        }));
        var sb = new StringBuilder("Sources:\n");
        for (var i = 0; i < reads.Length; i++)
        {
            sb.Append('[').Append(i + 1).Append("] ").Append(reads[i].Hit.Title).Append(" - ").Append(reads[i].Hit.Url);
            if (reads[i].Text is null)
            {
                sb.Append(" (not read: ").Append(reads[i].Note).Append(')');
            }
            sb.Append('\n');
        }
        var budget = 12000 / Math.Max(1, reads.Count(r => r.Text is not null));
        for (var i = 0; i < reads.Length; i++)
        {
            var excerpt = reads[i].Text is { } t ? Excerpt.Best(t, question, budget) : reads[i].Hit.Snippet;
            if (excerpt.Length > 0)
            {
                sb.Append("\n[").Append(i + 1).Append("]\n").Append(excerpt).Append('\n');
            }
        }
        sb.Append("\nWrite a short answer from these sources. Put [n] after each claim. Say if the sources disagree or don't answer it.");
        return sb.ToString();
    }

    [Tool("web_login", Permission.Ask, TimeoutSeconds = 180)]
    [Description("Signs in to a website the user added in Settings, with the user name and password stored there, and keeps the session for web_read. Asks the user for a two-factor code if the site wants one. You never see the password.")]
    public async Task<string> Login([Description("The site's name or address as the user added it, for example \"github\"")] string site, CancellationToken ct)
    {
        var logins = settings.Current.Web.Logins;
        var login = logins.FirstOrDefault(l => l.Name.Equals(site.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? logins.FirstOrDefault(l => l.LoginUrl.Contains(site.Trim(), StringComparison.OrdinalIgnoreCase) || l.Name.Contains(site.Trim(), StringComparison.OrdinalIgnoreCase));
        if (login is null)
        {
            return logins.Count == 0
                ? "No logins are set up. The user adds a site, user name and password in Settings > Web."
                : $"No login for \"{site}\". Set up: {string.Join(", ", logins.Select(l => l.Name))}. Others are added in Settings > Web.";
        }
        var secret = secrets.Get(login.SecretKey) ?? throw new ToolException($"The password for {login.Name} is missing. The user adds it again in Settings > Web.");
        var url = UrlGuard.Parse(login.LoginUrl);
        await using var page = await browser.OpenAsync(url, [], ct);
        var state = "";
        for (var round = 0; round < 3; round++)
        {
            var filled = await page.EvaluateAsync(LoginScripts.Fill(secret.UserName, secret.Secret), ct);
            if (filled is "nofields")
            {
                state = filled;
                break;
            }
            await page.WaitForLoadAsync(TimeSpan.FromSeconds(12), ct);
            await Task.Delay(1000, ct);
            state = await page.EvaluateAsync(LoginScripts.State, ct);
            // Another round only after a user-name-only step. The password is sent once: if the field is
            // still there after that, it was refused, and sending it again could lock the account.
            if (state != "password" || filled != "user-only")
            {
                break;
            }
        }
        for (var round = 0; state == "code" && round < 2; round++)
        {
            var code = await prompt.AskTextAsync($"{login.Name} sign-in", $"{login.Name} wants a sign-in code. Type or say the code.", ct);
            if (string.IsNullOrWhiteSpace(code))
            {
                return $"Sign-in to {login.Name} stopped: no code was given.";
            }
            await page.EvaluateAsync(LoginScripts.FillCode(code.Trim()), ct);
            await page.WaitForLoadAsync(TimeSpan.FromSeconds(12), ct);
            await Task.Delay(1000, ct);
            state = await page.EvaluateAsync(LoginScripts.State, ct);
        }
        switch (state)
        {
            case "nofields":
                return $"Could not find the sign-in form on {url.Host}. The login address in Settings may be wrong.";
            case "password":
                return $"Sign-in to {login.Name} did not work: the site still shows the password field. The user name or password may be wrong.";
            case "code":
                return $"Sign-in to {login.Name} did not work: the site did not take the code.";
        }
        sessions.Save(page.Url.Host, await page.CookiesAsync(ct));
        Log.Information("Signed in to {Site}", login.Name);
        return $"Signed in to {login.Name}. web_read now uses this session for {SessionVault.SiteOf(page.Url.Host)}.";
    }

    private async Task<(List<SearchHit> Hits, string Engine)> SearchHitsAsync(string query, CancellationToken ct)
    {
        var web = settings.Current.Web;
        var key = secrets.Get(BraveKey)?.Secret;
        var engine = SearchEngines.Choose(web.SearchProvider, web.SearxngUrl, !string.IsNullOrWhiteSpace(key));
        try
        {
            switch (engine)
            {
                case "searxng":
                    var sx = await localHttp.GetAsync(SearchEngines.SearxngUri(web.SearxngUrl, query), ct);
                    Check(sx, "SearXNG");
                    return (SearchEngines.ParseSearxng(sx.Body), "SearXNG");
                case "brave":
                    var br = await http.GetAsync(SearchEngines.BraveUri(query), ct, [("X-Subscription-Token", key!), ("Accept", "application/json")]);
                    Check(br, "Brave Search");
                    return (SearchEngines.ParseBrave(br.Body), "Brave Search");
                default:
                    var form = new FormUrlEncodedContent([new("q", query), new("kl", "wt-wt")]);
                    var dd = await http.GetAsync(SearchEngines.DuckDuckGoUri, ct, post: form);
                    Check(dd, "DuckDuckGo");
                    var ddHits = SearchEngines.ParseDuckDuckGo(dd.Body);
                    if (ddHits.Count == 0 && SearchEngines.IsDuckDuckGoBlock(dd.Status, dd.Body))
                    {
                        throw new ToolException("DuckDuckGo blocked the search for now. Try again later, or set up Brave Search or SearXNG in Settings > Web.");
                    }
                    return (ddHits, "DuckDuckGo");
            }
        }
        catch (HttpRequestException ex)
        {
            throw new ToolException($"The search engine could not be reached: {ex.Message}");
        }
        catch (JsonException)
        {
            throw new ToolException("The search engine sent an answer Starlight could not read.");
        }
    }

    private sealed record SourceRead(SearchHit Hit, string? Text, string? Note);

    private static void Check(FetchedPage page, string engine)
    {
        if (page.Status == 401 || page.Status == 403)
        {
            throw new ToolException($"{engine} refused the request ({page.Status}). Check the key or address in Settings > Web.");
        }
        if (page.Status == 429)
        {
            throw new ToolException($"{engine} says too many searches. Try again in a minute.");
        }
        if (page.Status >= 400)
        {
            throw new ToolException($"{engine} answered with error {page.Status}.");
        }
    }

    private async Task<CleanPage> ReadPageAsync(Uri url, CancellationToken ct)
    {
        var cookies = sessions.Load(url.Host);
        if (cookies.Count > 0)
        {
            var (final, html) = await browser.RenderAsync(url, cookies, ct);
            return PageText.Clean(html) with { Url = final };
        }
        FetchedPage fetched;
        try
        {
            fetched = await http.GetAsync(url, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new ToolException($"{url.Host} could not be reached: {ex.Message}");
        }
        if (fetched.Status >= 400)
        {
            throw new ToolException($"{url.Host} answered with error {fetched.Status}.");
        }
        if (fetched.ContentType is "text/plain" or "application/json" or "text/markdown" or "text/csv")
        {
            return new CleanPage(url.Host, fetched.Body, false) { Url = fetched.Url };
        }
        if (!fetched.ContentType.Contains("html", StringComparison.OrdinalIgnoreCase) && fetched.ContentType.Length > 0)
        {
            throw new ToolException($"{url} is a {fetched.ContentType} file, not a page Starlight can read.");
        }
        var page = PageText.Clean(fetched.Body) with { Url = fetched.Url };
        if (!page.NeedsScripts)
        {
            return page;
        }
        Log.Information("Page {Url} needs scripts; using the browser", url);
        var (u, rendered) = await browser.RenderAsync(url, [], ct);
        return PageText.Clean(rendered) with { Url = u };
    }

    private async Task<bool> RobotsAllowAsync(Uri url, CancellationToken ct)
    {
        RobotsRules rules;
        lock (_robots)
        {
            if (_robots.TryGetValue(url.Host, out var cached) && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromHours(1))
            {
                return cached.Rules.Allows(url.PathAndQuery);
            }
        }
        try
        {
            var r = await http.GetAsync(new Uri(url, "/robots.txt"), ct);
            rules = r.Status is >= 200 and < 300 ? RobotsRules.Parse(r.Body) : RobotsRules.AllowAll;
        }
        catch (HttpRequestException)
        {
            rules = RobotsRules.AllowAll;
        }
        lock (_robots)
        {
            _robots[url.Host] = (rules, DateTimeOffset.UtcNow);
        }
        return rules.Allows(url.PathAndQuery);
    }
}
