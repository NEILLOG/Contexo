namespace Contexo.Core.Abstractions;

/// <summary>Kind of content a <see cref="DocumentSection"/> carries. Drives chunking rules.</summary>
public enum SectionKind
{
    /// <summary>Running prose: paragraphs, lists. May be split by the chunker.</summary>
    Prose,
    /// <summary>A table rendered as HTML. Must not be split mid-row.</summary>
    Table,
    /// <summary>One whole slide (title, body, shapes, tables). Kept whole when possible.</summary>
    Slide,
    /// <summary>Speaker notes of a slide.</summary>
    Notes,
    /// <summary>A diagram rendered as text, e.g. Mermaid "A --> B" lines.</summary>
    Diagram,
    /// <summary>Description of a large spreadsheet table that is queried with SQL instead of embedded row by row.</summary>
    TableSummary,
}

/// <summary>Stable error codes shown to users (mapped to plain-language Chinese text in the UI).</summary>
public enum DocumentErrorCode
{
    None = 0,
    PasswordProtected,
    Locked,
    Corrupted,
    TooLarge,
    Unsupported,
    Timeout,
    AccessDenied,
    Unknown,
}

/// <summary>Thrown by parsers for expected, user-facing failures. Anything else is treated as <see cref="DocumentErrorCode.Unknown"/>.</summary>
public sealed class DocumentParseException : Exception
{
    public DocumentParseException(DocumentErrorCode code, string message, Exception? inner = null)
        : base(message, inner) => Code = code;

    public DocumentErrorCode Code { get; }
}

/// <summary>Where a piece of content came from inside its file. All fields optional; set what applies.</summary>
public sealed record SourceLocation
{
    /// <summary>1-based PDF page number.</summary>
    public int? Page { get; init; }
    /// <summary>1-based slide number.</summary>
    public int? Slide { get; init; }
    /// <summary>Worksheet name.</summary>
    public string? Sheet { get; init; }
    /// <summary>A1-style range, e.g. "A1:F24".</summary>
    public string? CellRange { get; init; }
    /// <summary>Heading path from the top of the document, e.g. ["採購規範", "第 3 章 驗收", "3.2 驗收標準"].</summary>
    public IReadOnlyList<string>? HeadingPath { get; init; }
    /// <summary>Slide title or table caption, when there is one.</summary>
    public string? Title { get; init; }
    /// <summary>Chain of embedded files, outermost first, e.g. ["內嵌.xlsx"]. Null for content of the file itself.</summary>
    public IReadOnlyList<string>? EmbeddedPath { get; init; }

    public static SourceLocation None { get; } = new();
}

/// <summary>A contiguous piece of extracted content. Parsers emit these in reading order.</summary>
/// <param name="Text">Plain text; tables are embedded as HTML produced by <c>HtmlTableRenderer</c>.</param>
/// <param name="KeepWhole">When true the chunker must not split this section (unless it exceeds the hard limit).</param>
/// <param name="TableKey">For <see cref="SectionKind.TableSummary"/>: equals <see cref="SpreadsheetTable.TableKey"/> of the table it describes.</param>
public sealed record DocumentSection(
    SectionKind Kind,
    string Text,
    SourceLocation Location,
    bool KeepWhole = false,
    string? TableKey = null);

/// <summary>A file embedded inside another (OLE package, attachment). The pipeline parses it recursively.</summary>
public sealed record EmbeddedFile(string FileName, byte[] Content, SourceLocation ContainerLocation);

/// <summary>An image found inside a document. Version 1 only counts them; phase 2 sends them to OCR / a vision model.</summary>
/// <param name="ContextText">Nearby text (slide title, surrounding paragraph) to give a vision model context.</param>
public sealed record ExtractedImage(string FileName, string ContentType, byte[] Content, SourceLocation Location, string? ContextText);

/// <summary>A large spreadsheet region registered for SQL querying instead of row-by-row embedding.</summary>
/// <param name="TableKey">Unique within the file: "{Sheet}!{CellRange}".</param>
/// <param name="HeaderRowCount">Number of header rows at the top of <see cref="CellRange"/> (multi-level headers are joined with "_").</param>
/// <param name="Columns">Final column names after joining multi-level headers. Original text, not yet SQL-safe.</param>
/// <param name="SampleRows">First few data rows as display strings.</param>
/// <param name="Description">The text that gets embedded: file, sheet, columns, sample rows.</param>
public sealed record SpreadsheetTable(
    string TableKey,
    string Sheet,
    string CellRange,
    int HeaderRowCount,
    IReadOnlyList<string> Columns,
    int DataRowCount,
    IReadOnlyList<IReadOnlyList<string>> SampleRows,
    string Description);

public sealed record ParsedDocument(
    IReadOnlyList<DocumentSection> Sections,
    IReadOnlyList<EmbeddedFile> EmbeddedFiles,
    IReadOnlyList<ExtractedImage> Images,
    IReadOnlyList<SpreadsheetTable> Tables,
    IReadOnlyList<string> Warnings)
{
    public static ParsedDocument Empty { get; } = new([], [], [], [], []);
}

public sealed record ParserOptions
{
    /// <summary>A detected spreadsheet region with at most this many cells is rendered whole as an HTML table chunk.</summary>
    public int SmallTableMaxCells { get; init; } = 400;
    /// <summary>Number of sample data rows kept in <see cref="SpreadsheetTable.SampleRows"/>.</summary>
    public int TableSampleRows { get; init; } = 5;
    /// <summary>Hard cap on characters extracted from one file; extra content is dropped with a warning.</summary>
    public int MaxExtractedChars { get; init; } = 5_000_000;
}

/// <param name="Content">Readable, seekable stream of the whole file. Parsers must not dispose it.</param>
/// <param name="FileName">File name with extension, e.g. "報價單.xlsx". Used for titles and descriptions.</param>
/// <param name="EmbeddedPath">Non-null when parsing a file embedded in another; copy into every <see cref="SourceLocation.EmbeddedPath"/>.</param>
public sealed record ParseContext(
    Stream Content,
    string FileName,
    ParserOptions Options,
    IReadOnlyList<string>? EmbeddedPath = null);

public interface IDocumentParser
{
    /// <summary>Lower-case extensions including the dot, e.g. ".docx".</summary>
    IReadOnlyCollection<string> SupportedExtensions { get; }

    /// <summary>Extracts content. Throw <see cref="DocumentParseException"/> for expected failures.</summary>
    Task<ParsedDocument> ParseAsync(ParseContext context, CancellationToken cancellationToken);
}

public interface IParserRegistry
{
    /// <summary>Returns the parser for an extension (case-insensitive, with dot) or null when unsupported.</summary>
    IDocumentParser? Resolve(string extension);

    IReadOnlyCollection<string> SupportedExtensions { get; }
}

/// <summary>Raw cells of a spreadsheet region, used by the table query service. Implemented by the Excel/CSV parser task.</summary>
/// <param name="Rows">Data rows only (header rows removed), each with exactly <c>Columns.Count</c> display strings (null for empty).</param>
public sealed record SpreadsheetRegion(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string?>> Rows);

public interface ISpreadsheetRegionReader
{
    /// <summary>Reads a registered region from an .xlsx or .csv file. Re-applies the same header joining as the parser.</summary>
    Task<SpreadsheetRegion> ReadAsync(string filePath, string sheet, string cellRange, int headerRowCount, CancellationToken cancellationToken);
}
