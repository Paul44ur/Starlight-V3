using System.Net;
using System.Text.Json;
using AngleSharp.Html.Parser;

namespace Starlight.Tools.Web;

public sealed record SearchHit(string Title, string Url, string Snippet);

/// <summary>
/// The search engines (plan 5.8): the user's SearXNG if set, else Brave Search with the user's free key,
/// else DuckDuckGo's HTML page. Parsing is separate from fetching so it can be tested.
/// </summary>
public static class SearchEngines
{
    public static string Choose(string provider, string searxngUrl, bool hasBraveKey) => provider.ToLowerInvariant() switch
    {
        "searxng" when !string.IsNullOrWhiteSpace(searxngUrl) => "searxng",
        "brave" when hasBraveKey => "brave",
        "duckduckgo" => "duckduckgo",
        _ when !string.IsNullOrWhiteSpace(searxngUrl) => "searxng",
        _ when hasBraveKey => "brave",
        _ => "duckduckgo",
    };

    public static Uri SearxngUri(string baseUrl, string query) =>
        new(baseUrl.TrimEnd('/') + "/search?format=json&q=" + Uri.EscapeDataString(query));

    public static Uri BraveUri(string query) =>
        new("https://api.search.brave.com/res/v1/web/search?count=10&q=" + Uri.EscapeDataString(query));

    public static readonly Uri DuckDuckGoUri = new("https://html.duckduckgo.com/html/");

    public static List<SearchHit> ParseSearxng(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results))
        {
            return [];
        }
        return results.EnumerateArray()
            .Select(r => new SearchHit(Str(r, "title"), Str(r, "url"), Str(r, "content")))
            .Where(h => h.Url.Length > 0).ToList();
    }

    public static List<SearchHit> ParseBrave(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("web", out var web) || !web.TryGetProperty("results", out var results))
        {
            return [];
        }
        return results.EnumerateArray()
            .Select(r => new SearchHit(StripTags(Str(r, "title")), Str(r, "url"), StripTags(Str(r, "description"))))
            .Where(h => h.Url.Length > 0).ToList();
    }

    public static List<SearchHit> ParseDuckDuckGo(string html)
    {
        var doc = new HtmlParser().ParseDocument(html);
        var hits = new List<SearchHit>();
        foreach (var result in doc.QuerySelectorAll(".result"))
        {
            if (result.ClassList.Contains("result--ad"))
            {
                continue;
            }
            var link = result.QuerySelector("a.result__a");
            var href = link?.GetAttribute("href");
            if (link is null || string.IsNullOrEmpty(href))
            {
                continue;
            }
            var url = RealUrl(href);
            if (url is null)
            {
                continue;
            }
            hits.Add(new SearchHit(PageText.Squash(link.TextContent), url, PageText.Squash(result.QuerySelector(".result__snippet")?.TextContent ?? "")));
        }
        return hits;
    }

    /// <summary>DuckDuckGo wraps links as //duckduckgo.com/l/?uddg=REAL; ads go through y.js.</summary>
    internal static string? RealUrl(string href)
    {
        if (href.StartsWith("//", StringComparison.Ordinal))
        {
            href = "https:" + href;
        }
        if (!Uri.TryCreate(href, UriKind.Absolute, out var uri))
        {
            return null;
        }
        if (uri.Host.EndsWith("duckduckgo.com", StringComparison.OrdinalIgnoreCase))
        {
            if (uri.AbsolutePath.StartsWith("/y.js", StringComparison.Ordinal))
            {
                return null;
            }
            var q = uri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).FirstOrDefault(p => p[0] == "uddg");
            return q is { Length: 2 } ? WebUtility.UrlDecode(q[1]) : null;
        }
        return uri.ToString();
    }

    public static string Format(IReadOnlyList<SearchHit> hits, string query, string engine)
    {
        if (hits.Count == 0)
        {
            return $"No results for \"{query}\".";
        }
        var lines = hits.Take(8).Select((h, i) => $"{i + 1}. {h.Title}\n   {h.Url}\n   {Short(h.Snippet, 220)}");
        return $"Results for \"{query}\" ({engine}):\n" + string.Join('\n', lines);
    }

    private static string Short(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string StripTags(string s) => PageText.Squash(WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(s, "<[^>]+>", "")));
}
