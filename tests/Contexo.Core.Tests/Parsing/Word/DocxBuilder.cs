using System.Security;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Spreadsheet = DocumentFormat.OpenXml.Spreadsheet;

namespace Contexo.Core.Tests.Parsing.Word;

/// <summary>Builds small .docx files in memory for the Word parser tests.</summary>
internal sealed class DocxBuilder
{
    public const string NamespaceDeclarations =
        "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" " +
        "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" " +
        "xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\" " +
        "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" " +
        "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
        "xmlns:wps=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\" " +
        "xmlns:v=\"urn:schemas-microsoft-com:vml\" " +
        "xmlns:o=\"urn:schemas-microsoft-com:office:office\"";

    // 1x1 transparent PNG.
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    private readonly List<string> _styles = [];
    private readonly List<string> _sectionProperties = [];
    private readonly MemoryStream _stream = new();
    private readonly WordprocessingDocument _document;
    private int? _defaultSize;

    public DocxBuilder()
    {
        _document = WordprocessingDocument.Create(_stream, DocumentFormat.OpenXml.WordprocessingDocumentType.Document);
        Main = _document.AddMainDocumentPart();
        Main.Document = new Document(new Body());
        Body = Main.Document.Body!;
    }

    public MainDocumentPart Main { get; }

    public Body Body { get; }

    public static string Escape(string text) => SecurityElement.Escape(text) ?? string.Empty;

    // ---- styles ----

    public DocxBuilder DefaultFontSize(int halfPoints)
    {
        _defaultSize = halfPoints;
        return this;
    }

    public DocxBuilder AddStyle(string id, string name, string? basedOn = null, int? outline = null, bool? bold = null, int? size = null, bool isDefault = false)
    {
        var xml = $"<w:style w:type=\"paragraph\" w:styleId=\"{id}\"{(isDefault ? " w:default=\"1\"" : "")}><w:name w:val=\"{Escape(name)}\"/>";
        if (basedOn is not null)
        {
            xml += $"<w:basedOn w:val=\"{basedOn}\"/>";
        }

        if (outline is not null)
        {
            xml += $"<w:pPr><w:outlineLvl w:val=\"{outline}\"/></w:pPr>";
        }

        if (bold is not null || size is not null)
        {
            xml += "<w:rPr>" + (bold is true ? "<w:b/>" : bold is false ? "<w:b w:val=\"0\"/>" : "") +
                   (size is not null ? $"<w:sz w:val=\"{size}\"/>" : "") + "</w:rPr>";
        }

        _styles.Add(xml + "</w:style>");
        return this;
    }

    /// <summary>Normal plus Heading1..3 with english built-in names and outline levels.</summary>
    public DocxBuilder AddHeadingStyles()
    {
        AddStyle("Normal", "Normal", isDefault: true);
        for (var level = 1; level <= 3; level++)
        {
            AddStyle($"Heading{level}", $"heading {level}", basedOn: "Normal", outline: level - 1, bold: true, size: 40 - (level * 4));
        }

        return this;
    }

    /// <summary>Heading styles that only carry a Chinese name (no outline level), like a localized Word installation can produce.</summary>
    public DocxBuilder AddChineseHeadingStyles()
    {
        AddStyle("Normal", "Normal", isDefault: true);
        AddStyle("a3", "標題 1", basedOn: "Normal", bold: true);
        AddStyle("a4", "標題 2", basedOn: "Normal", bold: true);
        return this;
    }

    // ---- body content ----

    public static string RunXml(string text, bool bold = false, int? size = null)
    {
        var properties = bold || size is not null
            ? "<w:rPr>" + (bold ? "<w:b/>" : "") + (size is not null ? $"<w:sz w:val=\"{size}\"/>" : "") + "</w:rPr>"
            : "";
        return $"<w:r>{properties}<w:t xml:space=\"preserve\">{Escape(text)}</w:t></w:r>";
    }

    /// <summary>Appends a paragraph built from raw inner XML (runs, ins, del, ...).</summary>
    public Paragraph Xml(string innerXml, string? style = null, bool list = false)
    {
        var properties = "";
        if (style is not null || list)
        {
            properties = "<w:pPr>" + (style is not null ? $"<w:pStyle w:val=\"{style}\"/>" : "") +
                         (list ? "<w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"1\"/></w:numPr>" : "") + "</w:pPr>";
        }

        var paragraph = new Paragraph($"<w:p {NamespaceDeclarations}>{properties}{innerXml}</w:p>");
        Body.Append(paragraph);
        return paragraph;
    }

    public Paragraph Para(string text, string? style = null, bool bold = false, int? size = null, bool list = false) =>
        Xml(RunXml(text, bold, size), style, list);

    public Paragraph Heading(int level, string text) => Para(text, $"Heading{level}");

    public Table Table(string rowsXml, string? tablePropertiesXml = null)
    {
        var table = new Table($"<w:tbl {NamespaceDeclarations}><w:tblPr>{tablePropertiesXml}</w:tblPr>{rowsXml}</w:tbl>");
        Body.Append(table);
        return table;
    }

    public static string Cell(string text, int gridSpan = 1, string? vMerge = null)
    {
        var properties = "";
        if (gridSpan > 1)
        {
            properties += $"<w:gridSpan w:val=\"{gridSpan}\"/>";
        }

        if (vMerge is not null)
        {
            properties += vMerge == "continue" ? "<w:vMerge/>" : $"<w:vMerge w:val=\"{vMerge}\"/>";
        }

        return $"<w:tc><w:tcPr>{properties}</w:tcPr><w:p>{(text.Length == 0 ? "" : RunXml(text))}</w:p></w:tc>";
    }

    public static string Row(string cellsXml, bool header = false) =>
        $"<w:tr><w:trPr>{(header ? "<w:tblHeader/>" : "")}</w:trPr>{cellsXml}</w:tr>";

    public DocxBuilder AppendBodyXml(string xml)
    {
        // Parsed as a one-element wrapper so that any block level element can be supplied.
        var wrapper = new Body($"<w:body {NamespaceDeclarations}>{xml}</w:body>");
        foreach (var child in wrapper.ChildElements.ToList())
        {
            child.Remove();
            Body.Append(child);
        }

        return this;
    }

    // ---- other parts ----

    public DocxBuilder AddFootnotes(params (long Id, string Text)[] notes)
    {
        var part = Main.AddNewPart<FootnotesPart>();
        var xml = $"<w:footnotes {NamespaceDeclarations}>" +
                  "<w:footnote w:type=\"separator\" w:id=\"-1\"><w:p><w:r><w:separator/></w:r></w:p></w:footnote>" +
                  "<w:footnote w:type=\"continuationSeparator\" w:id=\"0\"><w:p><w:r><w:continuationSeparator/></w:r></w:p></w:footnote>" +
                  string.Concat(notes.Select(n => $"<w:footnote w:id=\"{n.Id}\"><w:p><w:r><w:footnoteRef/></w:r>{RunXml(n.Text)}</w:p></w:footnote>")) +
                  "</w:footnotes>";
        part.Footnotes = new Footnotes(xml);
        return this;
    }

    public DocxBuilder AddEndnotes(params (long Id, string Text)[] notes)
    {
        var part = Main.AddNewPart<EndnotesPart>();
        var xml = $"<w:endnotes {NamespaceDeclarations}>" +
                  string.Concat(notes.Select(n => $"<w:endnote w:id=\"{n.Id}\"><w:p><w:r><w:endnoteRef/></w:r>{RunXml(n.Text)}</w:p></w:endnote>")) +
                  "</w:endnotes>";
        part.Endnotes = new Endnotes(xml);
        return this;
    }

    public DocxBuilder AddComments(params (long Id, string Text)[] comments)
    {
        var part = Main.AddNewPart<WordprocessingCommentsPart>();
        var xml = $"<w:comments {NamespaceDeclarations}>" +
                  string.Concat(comments.Select(c => $"<w:comment w:id=\"{c.Id}\" w:author=\"tester\"><w:p>{RunXml(c.Text)}</w:p></w:comment>")) +
                  "</w:comments>";
        part.Comments = new Comments(xml);
        return this;
    }

    public DocxBuilder AddHeaderAndFooter(string headerText, string footerText)
    {
        var header = Main.AddNewPart<HeaderPart>();
        header.Header = new Header($"<w:hdr {NamespaceDeclarations}><w:p>{RunXml(headerText)}</w:p></w:hdr>");
        var footer = Main.AddNewPart<FooterPart>();
        footer.Footer = new Footer($"<w:ftr {NamespaceDeclarations}><w:p>{RunXml(footerText)}</w:p></w:ftr>");
        _sectionProperties.Add(
            $"<w:headerReference w:type=\"default\" r:id=\"{Main.GetIdOfPart(header)}\"/>" +
            $"<w:footerReference w:type=\"default\" r:id=\"{Main.GetIdOfPart(footer)}\"/>");
        return this;
    }

    /// <summary>Adds an embedded workbook and returns the relationship id to reference from an OLE object.</summary>
    public string AddEmbeddedWorkbook()
    {
        using var workbookStream = new MemoryStream();
        using (var workbook = SpreadsheetDocument.Create(workbookStream, DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = workbook.AddWorkbookPart();
            workbookPart.Workbook = new Spreadsheet.Workbook(new Spreadsheet.Sheets());
            var sheetPart = workbookPart.AddNewPart<WorksheetPart>();
            sheetPart.Worksheet = new Spreadsheet.Worksheet(new Spreadsheet.SheetData());
            workbookPart.Workbook.GetFirstChild<Spreadsheet.Sheets>()!.Append(new Spreadsheet.Sheet
            {
                Id = workbookPart.GetIdOfPart(sheetPart),
                SheetId = 1,
                Name = "Sheet1",
            });
        }

        var embedded = Main.AddEmbeddedPackagePart("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        using (var target = embedded.GetStream(FileMode.Create, FileAccess.Write))
        {
            target.Write(workbookStream.ToArray());
        }

        return Main.GetIdOfPart(embedded);
    }

    /// <summary>Adds a PNG image and returns the relationship id.</summary>
    public string AddImage()
    {
        var image = Main.AddImagePart(ImagePartType.Png);
        using (var target = image.GetStream(FileMode.Create, FileAccess.Write))
        {
            target.Write(Png);
        }

        return Main.GetIdOfPart(image);
    }

    public byte[] Build()
    {
        var stylesXml = $"<w:styles {NamespaceDeclarations}>" +
                        (_defaultSize is { } size
                            ? $"<w:docDefaults><w:rPrDefault><w:rPr><w:sz w:val=\"{size}\"/></w:rPr></w:rPrDefault></w:docDefaults>"
                            : "") +
                        string.Concat(_styles) + "</w:styles>";
        if (_styles.Count > 0 || _defaultSize is not null)
        {
            var stylesPart = Main.AddNewPart<StyleDefinitionsPart>();
            stylesPart.Styles = new Styles(stylesXml);
        }

        if (_sectionProperties.Count > 0)
        {
            Body.Append(new SectionProperties($"<w:sectPr {NamespaceDeclarations}>{string.Concat(_sectionProperties)}</w:sectPr>"));
        }

        _document.Dispose();
        return _stream.ToArray();
    }
}
