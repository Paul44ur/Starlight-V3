using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdBlock = Markdig.Syntax.Block;
using MdInline = Markdig.Syntax.Inlines.Inline;
using MdTable = Markdig.Extensions.Tables.Table;
using WpfBlock = System.Windows.Documents.Block;
using WpfInline = System.Windows.Documents.Inline;
using WpfTable = System.Windows.Documents.Table;

namespace Starlight.App;

/// <summary>Renders an answer's Markdown as native WPF text (no browser): paragraphs, headings, lists, code, tables, links.</summary>
internal static class MarkdownView
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePipeTables().UseEmphasisExtras().Build();
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    public static FlowDocument Render(string markdown, Brush foreground, Brush codeBackground)
    {
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 14,
            Foreground = foreground,
            TextAlignment = TextAlignment.Left,
        };
        foreach (var block in Markdown.Parse(markdown, Pipeline))
        {
            if (Block(block, codeBackground) is { } b)
            {
                doc.Blocks.Add(b);
            }
        }
        return doc;
    }

    private static WpfBlock? Block(MdBlock block, Brush codeBackground)
    {
        switch (block)
        {
            case HeadingBlock h:
                var hp = new Paragraph { FontWeight = FontWeights.SemiBold, FontSize = h.Level switch { 1 => 20, 2 => 18, _ => 16 }, Margin = new Thickness(0, 8, 0, 4) };
                AddInlines(hp.Inlines, h.Inline, codeBackground);
                return hp;
            case ParagraphBlock p:
                var para = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
                AddInlines(para.Inlines, p.Inline, codeBackground);
                return para;
            case FencedCodeBlock or CodeBlock:
                var code = (LeafBlock)block;
                var text = string.Join("\n", code.Lines.Lines.Take(code.Lines.Count).Select(l => l.ToString()));
                return new Paragraph(new Run(text))
                {
                    FontFamily = Mono, FontSize = 13, Background = codeBackground, Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 0, 8),
                };
            case ListBlock l:
                var list = new List { MarkerStyle = l.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc, Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(20, 0, 0, 0) };
                foreach (var item in l.OfType<ListItemBlock>())
                {
                    var li = new ListItem();
                    foreach (var child in item)
                    {
                        if (Block(child, codeBackground) is { } b)
                        {
                            if (b is Paragraph bp)
                            {
                                bp.Margin = new Thickness(0, 0, 0, 2);
                            }
                            li.Blocks.Add(b);
                        }
                    }
                    list.ListItems.Add(li);
                }
                return list;
            case QuoteBlock q:
                var section = new Section { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 0, 0, 0), Margin = new Thickness(0, 0, 0, 8) };
                foreach (var child in q)
                {
                    if (Block(child, codeBackground) is { } b)
                    {
                        section.Blocks.Add(b);
                    }
                }
                return section;
            case MdTable t:
                var table = new WpfTable { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 8), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(0.5) };
                var group = new TableRowGroup();
                foreach (var row in t.OfType<Markdig.Extensions.Tables.TableRow>())
                {
                    var tr = new System.Windows.Documents.TableRow();
                    foreach (var cell in row.OfType<Markdig.Extensions.Tables.TableCell>())
                    {
                        var cp = new Paragraph { Margin = new Thickness(6, 3, 6, 3), FontWeight = row.IsHeader ? FontWeights.SemiBold : FontWeights.Normal };
                        if (cell.FirstOrDefault() is ParagraphBlock pb)
                        {
                            AddInlines(cp.Inlines, pb.Inline, codeBackground);
                        }
                        tr.Cells.Add(new System.Windows.Documents.TableCell(cp) { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(0.5) });
                    }
                    group.Rows.Add(tr);
                }
                table.RowGroups.Add(group);
                return table;
            case ThematicBreakBlock:
                return new Paragraph { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(0, 0.5, 0, 0), Margin = new Thickness(0, 4, 0, 8) };
            default:
                return null;
        }
    }

    private static void AddInlines(InlineCollection target, ContainerInline? container, Brush codeBackground)
    {
        if (container is null)
        {
            return;
        }
        foreach (var inline in container)
        {
            if (Inline(inline, codeBackground) is { } i)
            {
                target.Add(i);
            }
        }
    }

    private static WpfInline? Inline(MdInline inline, Brush codeBackground)
    {
        switch (inline)
        {
            case LiteralInline lit:
                return new Run(lit.Content.ToString());
            case CodeInline code:
                return new Run(code.Content) { FontFamily = Mono, Background = codeBackground };
            case LineBreakInline:
                return new LineBreak();
            case EmphasisInline em:
                var span = em.DelimiterCount >= 2 ? (Span)new Bold() : new Italic();
                AddInlines(span.Inlines, em, codeBackground);
                return span;
            case LinkInline link when !link.IsImage:
                var h = new Hyperlink();
                AddInlines(h.Inlines, link, codeBackground);
                if (Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                {
                    h.NavigateUri = uri;
                    // The text of a link can say anything: the real address is shown on hover.
                    h.ToolTip = uri.AbsoluteUri;
                    h.RequestNavigate += (_, e) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
                }
                return h;
            case ContainerInline c:
                var s = new Span();
                AddInlines(s.Inlines, c, codeBackground);
                return s;
            default:
                return new Run(inline.ToString());
        }
    }
}
