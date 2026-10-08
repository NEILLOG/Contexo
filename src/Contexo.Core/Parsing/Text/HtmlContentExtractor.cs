using System.Text;
using System.Text.RegularExpressions;
using Contexo.Core.Abstractions;
using Contexo.Core.Common;
using HtmlAgilityPack;

namespace Contexo.Core.Parsing.Text;

/// <summary>
/// Turns an HTML string into document sections. Shared by <see cref="HtmlParser"/> and <see cref="RtfParser"/>
/// (RTF is converted to HTML first).
/// </summary>
internal sealed partial class HtmlContentExtractor
{
    private const int MaxDepth = 400;
    private const int MaxColumnSpan = 200;
    private const int MaxRowSpan = 1000;

    private static readonly HashSet<string> RemovedElements = new(StringComparer.Ordinal)
    {
        "script", "style", "noscript", "nav", "footer", "header", "head", "template", "svg", "iframe", "object",
    };

    private static readonly HashSet<string> BlockElements = new(StringComparer.Ordinal)
    {
        "p", "div", "li", "ul", "ol", "dl", "dt", "dd", "blockquote", "section", "article", "aside", "main", "form",
        "fieldset", "figure", "figcaption", "address", "details", "summary", "hr", "tr", "caption", "table", "thead",
        "tbody", "tfoot", "center", "body", "html", "h1", "h2", "h3", "h4", "h5", "h6", "pre", "legend", "menu", "dir",
    };

    private readonly ParseContext _context;
    private readonly List<DocumentSection> _sections = [];
    private readonly List<(int Level, string Text)> _headings = [];
    private TextBuffer _buffer = new();
    private string? _headingLine;

    private HtmlContentExtractor(ParseContext context) => _context = context;

    public static ParsedDocument Extract(string html, ParseContext context, CancellationToken cancellationToken)
    {
        var extractor = new HtmlContentExtractor(context);
        var warnings = new List<string>();
        var sections = extractor.Run(html, cancellationToken);
        sections = ParserSupport.ApplyLimit(sections, context.Options.MaxExtractedChars, warnings);
        return ParserSupport.ToDocument(sections, warnings);
    }

    private List<DocumentSection> Run(string html, CancellationToken cancellationToken)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);

        RemoveUnwantedNodes(document.DocumentNode);
        cancellationToken.ThrowIfCancellationRequested();

        Walk(document.DocumentNode, _buffer, flat: false, depth: 0, cancellationToken);
        FlushProse(isFinal: true);
        return _sections;
    }

    private static void RemoveUnwantedNodes(HtmlNode root)
    {
        var doomed = root.Descendants()
            .Where(n => n.NodeType == HtmlNodeType.Element && RemovedElements.Contains(n.Name.ToLowerInvariant()))
            .ToList();
        foreach (var node in doomed)
        {
            node.Remove();
        }
    }

    private void Walk(HtmlNode node, TextBuffer buffer, bool flat, int depth, CancellationToken cancellationToken)
    {
        // The main buffer is replaced whenever a heading or table starts a new section, so never keep a stale reference.
        buffer = flat ? buffer : _buffer;
        switch (node.NodeType)
        {
            case HtmlNodeType.Text:
                buffer.AppendInline(Collapse(HtmlEntity.DeEntitize(((HtmlTextNode)node).Text)));
                return;
            case HtmlNodeType.Element:
                break;
            case HtmlNodeType.Document:
                WalkChildren(node, buffer, flat, depth, cancellationToken);
                return;
            default:
                return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (depth > MaxDepth)
        {
            buffer.AppendInline(Collapse(HtmlEntity.DeEntitize(node.InnerText)));
            return;
        }

        var name = node.Name.ToLowerInvariant();

        if (!flat && name.Length == 2 && name[0] == 'h' && name[1] is >= '1' and <= '6')
        {
            var title = Flatten(node, cancellationToken).Replace('\n', ' ').Trim();
            if (title.Length > 0)
            {
                StartHeading(name[1] - '0', title);
                return;
            }
        }

        if (!flat && name == "table")
        {
            AddTable(node, cancellationToken);
            return;
        }

        switch (name)
        {
            case "br":
                buffer.NewLine();
                return;
            case "pre":
                buffer.Break();
                buffer.AppendRaw(HtmlEntity.DeEntitize(node.InnerText).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim('\n'));
                buffer.Break();
                return;
            case "td":
            case "th":
                WalkChildren(node, buffer, flat, depth, cancellationToken);
                (flat ? buffer : _buffer).AppendInline(" ");
                return;
        }

        if (BlockElements.Contains(name))
        {
            buffer.Break();
            WalkChildren(node, buffer, flat, depth, cancellationToken);
            (flat ? buffer : _buffer).Break();
        }
        else
        {
            WalkChildren(node, buffer, flat, depth, cancellationToken);
        }
    }

    private void WalkChildren(HtmlNode node, TextBuffer buffer, bool flat, int depth, CancellationToken cancellationToken)
    {
        foreach (var child in node.ChildNodes)
        {
            Walk(child, buffer, flat, depth + 1, cancellationToken);
        }
    }

    private string Flatten(HtmlNode node, CancellationToken cancellationToken)
    {
        var buffer = new TextBuffer();
        WalkChildren(node, buffer, flat: true, depth: 0, cancellationToken);
        return buffer.ToString();
    }

    private void StartHeading(int level, string title)
    {
        FlushProse(isFinal: false);

        while (_headings.Count > 0 && _headings[^1].Level >= level)
        {
            _headings.RemoveAt(_headings.Count - 1);
        }

        _headings.Add((level, title));
        _headingLine = title;
        _buffer.AppendRaw(title);
        _buffer.Break();
    }

    /// <summary>
    /// Emits the pending prose. A section that holds only its own heading is dropped (its title lives on in the children's
    /// HeadingPath) unless it is the last thing in the document and nothing else was produced.
    /// </summary>
    private void FlushProse(bool isFinal)
    {
        var text = _buffer.ToString();
        var headingOnly = _headingLine is not null && text == _headingLine;
        _buffer = new TextBuffer();
        _headingLine = null;

        if (text.Length == 0)
        {
            return;
        }

        if (headingOnly && !(isFinal && _sections.Count == 0))
        {
            return;
        }

        _sections.Add(new DocumentSection(SectionKind.Prose, text, Location()));
    }

    private SourceLocation Location()
    {
        var path = _headings.Count == 0 ? null : _headings.Select(h => h.Text).ToArray();
        return ParserSupport.CreateLocation(_context, path);
    }

    private void AddTable(HtmlNode table, CancellationToken cancellationToken)
    {
        var model = BuildTableModel(table, cancellationToken);
        if (model is null)
        {
            return;
        }

        FlushProse(isFinal: false);
        _sections.Add(new DocumentSection(SectionKind.Table, HtmlTableRenderer.Render(model), Location(), KeepWhole: true));
    }

    private TableModel? BuildTableModel(HtmlNode table, CancellationToken cancellationToken)
    {
        var rows = table.Descendants("tr")
            .Where(tr => NearestAncestor(tr, "table") == table)
            .Select(tr => (Row: tr, Cells: tr.ChildNodes.Where(IsCell).ToList()))
            .Where(r => r.Cells.Count > 0)
            .ToList();
        if (rows.Count == 0)
        {
            return null;
        }

        var cells = new List<TableCell>();
        var occupied = new HashSet<(int Row, int Column)>();
        var columnCount = 0;
        for (var r = 0; r < rows.Count; r++)
        {
            var column = 0;
            foreach (var cellNode in rows[r].Cells)
            {
                while (occupied.Contains((r, column)))
                {
                    column++;
                }

                var colSpan = ParseSpan(cellNode, "colspan", MaxColumnSpan);
                var rowSpan = Math.Min(ParseSpan(cellNode, "rowspan", MaxRowSpan), rows.Count - r);
                for (var rr = r; rr < r + rowSpan; rr++)
                {
                    for (var cc = column; cc < column + colSpan; cc++)
                    {
                        occupied.Add((rr, cc));
                    }
                }

                cells.Add(new TableCell(r, column, Flatten(cellNode, cancellationToken), rowSpan, colSpan));
                column += colSpan;
                columnCount = Math.Max(columnCount, column);
            }
        }

        var headerRows = CountHeaderRows(rows);
        var captionNode = table.ChildNodes.FirstOrDefault(n => n.Name.Equals("caption", StringComparison.OrdinalIgnoreCase));
        var caption = captionNode is null ? null : Flatten(captionNode, cancellationToken).Replace('\n', ' ').Trim();

        return new TableModel(rows.Count, columnCount, cells, headerRows, string.IsNullOrEmpty(caption) ? null : caption);
    }

    private static int CountHeaderRows(List<(HtmlNode Row, List<HtmlNode> Cells)> rows)
    {
        var inThead = 0;
        while (inThead < rows.Count && NearestAncestor(rows[inThead].Row, "thead") is not null)
        {
            inThead++;
        }

        if (inThead > 0)
        {
            return inThead;
        }

        var allTh = 0;
        while (allTh < rows.Count && rows[allTh].Cells.All(c => c.Name.Equals("th", StringComparison.OrdinalIgnoreCase)))
        {
            allTh++;
        }

        return allTh;
    }

    private static bool IsCell(HtmlNode node) =>
        node.NodeType == HtmlNodeType.Element
        && (node.Name.Equals("td", StringComparison.OrdinalIgnoreCase) || node.Name.Equals("th", StringComparison.OrdinalIgnoreCase));

    private static int ParseSpan(HtmlNode cell, string attribute, int max)
    {
        var value = cell.GetAttributeValue(attribute, "1").Trim();
        return int.TryParse(value, out var span) ? Math.Clamp(span, 1, max) : 1;
    }

    /// <summary>The closest ancestor with the given tag name, stopping at the enclosing table for <c>thead</c> lookups.</summary>
    private static HtmlNode? NearestAncestor(HtmlNode node, string name)
    {
        for (var parent = node.ParentNode; parent is not null; parent = parent.ParentNode)
        {
            if (parent.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return parent;
            }

            if (name != "table" && parent.Name.Equals("table", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        return null;
    }

    private static string Collapse(string text) => WhitespaceRegex().Replace(text, " ");

    [GeneratedRegex(@"[\s ​﻿]+")]
    private static partial Regex WhitespaceRegex();

    /// <summary>Accumulates text with block / inline whitespace rules.</summary>
    private sealed class TextBuffer
    {
        private readonly StringBuilder _sb = new();

        public void AppendInline(string collapsed)
        {
            if (collapsed.Length == 0)
            {
                return;
            }

            if (collapsed[0] == ' ' && (_sb.Length == 0 || _sb[^1] is '\n' or ' '))
            {
                collapsed = collapsed.TrimStart(' ');
            }

            _sb.Append(collapsed);
        }

        public void AppendRaw(string text) => _sb.Append(text);

        /// <summary>Ends the current line if it has content (block boundary).</summary>
        public void Break()
        {
            TrimTrailingSpaces();
            if (_sb.Length > 0 && _sb[^1] != '\n')
            {
                _sb.Append('\n');
            }
        }

        /// <summary>Forced line break (&lt;br&gt;); never produces more than one blank line in a row.</summary>
        public void NewLine()
        {
            TrimTrailingSpaces();
            if (_sb.Length == 0 || (_sb.Length >= 2 && _sb[^1] == '\n' && _sb[^2] == '\n'))
            {
                return;
            }

            _sb.Append('\n');
        }

        public override string ToString() => _sb.ToString().Trim();

        private void TrimTrailingSpaces()
        {
            while (_sb.Length > 0 && _sb[^1] == ' ')
            {
                _sb.Length--;
            }
        }
    }
}
