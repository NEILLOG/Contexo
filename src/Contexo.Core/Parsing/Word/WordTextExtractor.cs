using System.Globalization;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Contexo.Core.Parsing.Word;

/// <summary>Text of one paragraph. <see cref="ExtraLines"/> holds text box content and notes that follow the paragraph.</summary>
internal sealed record ParagraphText(string Text, IReadOnlyList<string> ExtraLines);

/// <summary>
/// Reads visible text from Word paragraphs as if all tracked changes had been accepted.
/// One instance tracks field state (field code vs. displayed result) for one story, so use a new instance for text boxes, notes and table cells.
/// </summary>
internal sealed class WordTextExtractor
{
    private readonly WordContext _context;
    private readonly List<FieldState> _fields = [];

    public WordTextExtractor(WordContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    /// <summary>True when the last <see cref="ReadParagraph"/> call touched an automatic table of contents field.</summary>
    public bool TocTouched { get; private set; }

    public ParagraphText ReadParagraph(Paragraph paragraph)
    {
        TocTouched = _fields.Exists(static f => f.IsToc);

        var text = new StringBuilder();
        var extraLines = new List<string>();
        var notes = new List<(bool IsEndnote, long Id)>();
        Visit(paragraph, text, extraLines, notes);

        foreach (var (isEndnote, id) in notes)
        {
            var noteText = _context.GetNoteText(isEndnote, id);
            if (!string.IsNullOrEmpty(noteText))
            {
                extraLines.Add("〔註〕" + noteText);
            }
        }

        return new ParagraphText(text.ToString().Trim(), extraLines);
    }

    /// <summary>Reads paragraphs and tables below <paramref name="container"/> (a text box, note, comment or table cell) as lines of text.</summary>
    public List<string> ReadLines(OpenXmlElement container)
    {
        var lines = new List<string>();
        CollectLines(container, lines);
        return lines;
    }

    private void CollectLines(OpenXmlElement container, List<string> lines)
    {
        foreach (var child in container.ChildElements)
        {
            switch (child)
            {
                case Paragraph paragraph:
                {
                    var result = ReadParagraph(paragraph);
                    if (TocTouched)
                    {
                        break;
                    }

                    AddParagraphLines(lines, result, _context.Styles.IsListItem(paragraph));
                    break;
                }

                case Table table:
                    lines.AddRange(FlattenTable(table));
                    break;

                case SdtBlock sdt:
                    if (!WordBlocks.IsTableOfContents(sdt) && sdt.SdtContentBlock is { } content)
                    {
                        CollectLines(content, lines);
                    }

                    break;

                case CustomXmlBlock custom:
                    CollectLines(custom, lines);
                    break;
            }
        }
    }

    /// <summary>Adds the text lines of a paragraph; list items get a leading "- ".</summary>
    public static void AddParagraphLines(List<string> lines, ParagraphText result, bool isListItem)
    {
        if (result.Text.Length > 0)
        {
            lines.Add(isListItem ? "- " + result.Text : result.Text);
        }

        lines.AddRange(result.ExtraLines);
    }

    /// <summary>Renders a (nested) table as plain text: one line per row, cells separated by " | ".</summary>
    private List<string> FlattenTable(Table table)
    {
        var lines = new List<string>();
        foreach (var row in WordBlocks.EnumerateRows(table))
        {
            var cells = new List<string>();
            foreach (var cell in WordBlocks.EnumerateCells(row))
            {
                var cellText = string.Join(' ', new WordTextExtractor(_context).ReadLines(cell));
                if (cellText.Length > 0)
                {
                    cells.Add(cellText);
                }
            }

            if (cells.Count > 0)
            {
                lines.Add(string.Join(" | ", cells));
            }
        }

        return lines;
    }

    private void Visit(OpenXmlElement element, StringBuilder text, List<string> extraLines, List<(bool, long)> notes)
    {
        foreach (var child in element.ChildElements)
        {
            switch (child)
            {
                case Run run:
                    VisitRun(run, text, extraLines, notes);
                    break;

                // Accept-all-changes: deleted and moved-away content disappears.
                case DeletedRun:
                case MoveFromRun:
                case ParagraphProperties:
                case SdtProperties:
                case SdtEndCharProperties:
                    break;

                case SimpleField simple:
                    if (simple.Instruction?.Value is { } instruction && IsTocCode(instruction))
                    {
                        TocTouched = true;
                    }

                    Visit(simple, text, extraLines, notes);
                    break;

                default:
                    Visit(child, text, extraLines, notes);
                    break;
            }
        }
    }

    private void VisitRun(Run run, StringBuilder text, List<string> extraLines, List<(bool, long)> notes)
    {
        foreach (var item in run.ChildElements)
        {
            switch (item)
            {
                case FieldChar fieldChar:
                    HandleFieldChar(fieldChar);
                    continue;

                case FieldCode code:
                    if (_fields.Count > 0 && _fields[^1].InCode)
                    {
                        var field = _fields[^1];
                        field.Code.Append(code.Text);
                        if (field.IsToc)
                        {
                            TocTouched = true;
                        }
                    }

                    continue;
            }

            if (InFieldCode())
            {
                continue;
            }

            switch (item)
            {
                case DocumentFormat.OpenXml.Wordprocessing.Text t:
                    text.Append(t.Text);
                    break;

                case TabChar:
                case PositionalTab:
                    text.Append('\t');
                    break;

                case Break br:
                    // Page and column breaks do not produce text.
                    if (br.Type is null || br.Type.Value == BreakValues.TextWrapping)
                    {
                        text.Append('\n');
                    }

                    break;

                case CarriageReturn:
                    text.Append('\n');
                    break;

                case NoBreakHyphen:
                    text.Append('-');
                    break;

                case SymbolChar symbol:
                    AppendSymbol(text, symbol);
                    break;

                case FootnoteReference footnote when footnote.Id?.Value is { } footnoteId:
                    notes.Add((false, footnoteId));
                    break;

                case EndnoteReference endnote when endnote.Id?.Value is { } endnoteId:
                    notes.Add((true, endnoteId));
                    break;

                case Drawing:
                case Picture:
                case EmbeddedObject:
                case AlternateContent:
                    ScanGraphic(item, extraLines);
                    break;
            }
        }
    }

    private void HandleFieldChar(FieldChar fieldChar)
    {
        var type = fieldChar.FieldCharType?.Value;
        if (type == FieldCharValues.Begin)
        {
            _fields.Add(new FieldState());
        }
        else if (type == FieldCharValues.Separate)
        {
            if (_fields.Count > 0)
            {
                _fields[^1].InCode = false;
            }
        }
        else if (type == FieldCharValues.End && _fields.Count > 0)
        {
            _fields.RemoveAt(_fields.Count - 1);
        }
    }

    private bool InFieldCode()
    {
        foreach (var field in _fields)
        {
            if (field.InCode)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Walks a drawing / picture / OLE object: records referenced image and embedded file relationships and
    /// collects text box paragraphs. Only the first <c>mc:Choice</c> of an alternate content block is used
    /// so that text boxes are not read twice (the fallback is a VML copy).
    /// </summary>
    private void ScanGraphic(OpenXmlElement element, List<string> extraLines)
    {
        foreach (var attribute in element.GetAttributes())
        {
            if (attribute.NamespaceUri == WordContext.RelationshipNamespace && !string.IsNullOrEmpty(attribute.Value))
            {
                _context.AddReference(attribute.Value);
            }
        }

        foreach (var child in element.ChildElements)
        {
            switch (child.LocalName)
            {
                case "txbxContent":
                    extraLines.AddRange(new WordTextExtractor(_context).ReadLines(child));
                    break;

                case "AlternateContent":
                    foreach (var branch in child.ChildElements)
                    {
                        if (branch.LocalName == "Choice")
                        {
                            ScanGraphic(branch, extraLines);
                            break;
                        }
                    }

                    break;

                case "Fallback":
                    break;

                default:
                    ScanGraphic(child, extraLines);
                    break;
            }
        }
    }

    private static void AppendSymbol(StringBuilder text, SymbolChar symbol)
    {
        var hex = symbol.Char?.Value;
        if (string.IsNullOrEmpty(hex) || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
        {
            return;
        }

        if (code >= 0xF000)
        {
            // Private-use code points of symbol fonts (bullets, check marks) are decoration, not text.
            var font = symbol.Font?.Value ?? string.Empty;
            if (font.Contains("Symbol", StringComparison.OrdinalIgnoreCase)
                || font.Contains("Wingdings", StringComparison.OrdinalIgnoreCase)
                || font.Contains("Webdings", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            code -= 0xF000;
        }

        if (code >= 0x20)
        {
            text.Append(char.ConvertFromUtf32(code));
        }
    }

    internal static bool IsTocCode(string code) => code.AsSpan().TrimStart().StartsWith("TOC", StringComparison.OrdinalIgnoreCase);

    private sealed class FieldState
    {
        public bool InCode { get; set; } = true;

        public StringBuilder Code { get; } = new();

        public bool IsToc => IsTocCode(Code.ToString());
    }
}
