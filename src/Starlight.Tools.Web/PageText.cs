using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Starlight.Tools.Web;

public sealed record CleanPage(string Title, string Text, bool NeedsScripts)
{
    public Uri? Url { get; init; }
}

/// <summary>Turns HTML into plain text for the model: the main content, headings marked with #, lists with -,
/// no menus, scripts, styles or forms.</summary>
public static class PageText
{
    private static readonly string[] Drop = ["script", "style", "noscript", "template", "svg", "canvas", "iframe", "form", "nav", "footer", "aside", "button", "select", "dialog"];
    private static readonly string[] DropRoles = ["navigation", "banner", "contentinfo", "complementary", "search", "dialog", "alert"];
    private static readonly HashSet<string> Blocks = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "section", "article", "main", "header", "li", "tr", "br", "h1", "h2", "h3", "h4", "h5", "h6", "pre", "blockquote", "table", "ul", "ol", "dl", "dt", "dd", "figcaption",
    };

    public static CleanPage Clean(string html)
    {
        var doc = new HtmlParser().ParseDocument(html);
        var title = Squash(doc.Title ?? doc.QuerySelector("h1")?.TextContent ?? "");
        var scripts = doc.QuerySelectorAll("script").Length;
        foreach (var e in doc.QuerySelectorAll(string.Join(',', Drop)).ToList())
        {
            e.Remove();
        }
        foreach (var e in doc.QuerySelectorAll("[role]").Where(e => DropRoles.Contains(e.GetAttribute("role"), StringComparer.OrdinalIgnoreCase)).ToList())
        {
            e.Remove();
        }
        foreach (var e in doc.QuerySelectorAll("[hidden],[aria-hidden=true]").ToList())
        {
            e.Remove();
        }
        // The page's main part if it marks one; else the body without its header.
        var root = doc.QuerySelector("main") ?? doc.QuerySelector("article") ?? doc.QuerySelector("[role=main]") ?? (IElement?)doc.Body;
        if (root is null)
        {
            return new CleanPage(title, "", scripts > 0);
        }
        if (root == doc.Body)
        {
            foreach (var e in root.QuerySelectorAll("header").ToList())
            {
                e.Remove();
            }
        }
        var sb = new StringBuilder();
        Walk(root, sb);
        var lines = sb.ToString().Split('\n').Select(l => l.Trim()).ToList();
        var text = new StringBuilder();
        var blank = false;
        foreach (var line in lines)
        {
            if (line.Length == 0 || line == "-")
            {
                blank = text.Length > 0;
                continue;
            }
            if (blank)
            {
                text.Append('\n');
                blank = false;
            }
            text.Append(line).Append('\n');
        }
        var result = text.ToString().Trim();
        var needsScripts = result.Length < 300 && (scripts >= 3 || doc.QuerySelector("#root, #app, #__next, [data-reactroot], [ng-app]") is not null);
        return new CleanPage(title, result, needsScripts);
    }

    private static void Walk(INode node, StringBuilder sb)
    {
        foreach (var child in node.ChildNodes)
        {
            if (child is IText t)
            {
                var s = Squash(t.Data);
                if (s.Length > 0)
                {
                    if (sb.Length > 0 && sb[^1] != '\n' && sb[^1] != ' ')
                    {
                        sb.Append(' ');
                    }
                    sb.Append(s);
                }
                continue;
            }
            if (child is not IElement e)
            {
                continue;
            }
            var tag = e.LocalName;
            var block = Blocks.Contains(tag);
            if (block)
            {
                sb.Append('\n');
            }
            if (tag.Length == 2 && tag[0] == 'h' && char.IsDigit(tag[1]))
            {
                sb.Append(new string('#', tag[1] - '0')).Append(' ');
            }
            else if (tag == "li")
            {
                sb.Append("- ");
            }
            if (tag == "pre")
            {
                sb.Append(e.TextContent.TrimEnd()).Append('\n');
                continue;
            }
            if (tag == "img" && e.GetAttribute("alt") is { Length: > 0 } alt)
            {
                sb.Append("[image: ").Append(Squash(alt)).Append(']');
            }
            Walk(e, sb);
            if (block)
            {
                sb.Append('\n');
            }
            else if (tag is "td" or "th")
            {
                sb.Append(" | ");
            }
        }
    }

    internal static string Squash(string s)
    {
        var sb = new StringBuilder(s.Length);
        var space = false;
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch))
            {
                space = sb.Length > 0;
                continue;
            }
            if (space)
            {
                sb.Append(' ');
                space = false;
            }
            sb.Append(ch);
        }
        return sb.ToString();
    }
}
