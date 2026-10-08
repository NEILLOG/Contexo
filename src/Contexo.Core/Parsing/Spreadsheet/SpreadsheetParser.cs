using System.Text;
using System.Xml;
using Contexo.Core.Abstractions;
using Contexo.Core.Common;
using DocumentFormat.OpenXml.Packaging;

namespace Contexo.Core.Parsing.Spreadsheet;

/// <summary>
/// Reads .xlsx / .xlsm / .csv. Sheets and regions are split by size, not by purpose:
/// small ones become one HTML table section, large ones become a table description for embedding plus a registered
/// <see cref="SpreadsheetTable"/> that the table query service reads later.
/// </summary>
internal sealed class SpreadsheetParser : IDocumentParser
{
    private const int MaxSampleValueLength = 100;

    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".xlsx", ".xlsm", ".csv"];

    public Task<ParsedDocument> ParseAsync(ParseContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Task.Run(() => Parse(context, cancellationToken), cancellationToken);
    }

    private static ParsedDocument Parse(ParseContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var output = new Output(context);
        try
        {
            if (string.Equals(Path.GetExtension(context.FileName), ".csv", StringComparison.OrdinalIgnoreCase))
            {
                ParseCsv(context, output, cancellationToken);
            }
            else
            {
                ParseWorkbook(context, output, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is XmlException or InvalidDataException or DocumentFormat.OpenXml.Packaging.OpenXmlPackageException)
        {
            throw new DocumentParseException(DocumentErrorCode.Corrupted, "試算表內容損毀，無法讀取。", ex);
        }

        return output.ToDocument();
    }

    private static void ParseCsv(ParseContext context, Output output, CancellationToken cancellationToken)
    {
        if (context.Content.CanSeek)
        {
            context.Content.Position = 0;
        }

        var csv = CsvSheetCells.Load(context.Content);
        ProcessSheet(CsvSheetCells.SheetName, csv, singleRegion: true, context, output, cancellationToken);
    }

    private static void ParseWorkbook(ParseContext context, Output output, CancellationToken cancellationToken)
    {
        using var workbook = XlsxWorkbook.Open(context.Content);
        foreach (var sheet in workbook.Sheets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sheet.Hidden)
            {
                output.Warnings.Add("hidden-sheet:" + sheet.Name);
                continue;
            }

            ProcessSheet(sheet.Name, new XlsxSheetCells(workbook, sheet.Part), singleRegion: false, context, output, cancellationToken);
            ExtractEmbedded(sheet, context, output);
        }
    }

    private static void ExtractEmbedded(WorkbookSheet sheet, ParseContext context, Output output)
    {
        var location = new SourceLocation { Sheet = sheet.Name, EmbeddedPath = context.EmbeddedPath };
        output.EmbeddedFiles.AddRange(OfficeEmbeddedContent.ExtractEmbeddedFiles(sheet.Part, location));
        output.Images.AddRange(OfficeEmbeddedContent.ExtractImages(sheet.Part, location, sheet.Name));
        if (sheet.Part.DrawingsPart is { } drawings)
        {
            output.EmbeddedFiles.AddRange(OfficeEmbeddedContent.ExtractEmbeddedFiles(drawings, location));
            output.Images.AddRange(OfficeEmbeddedContent.ExtractImages(drawings, location, sheet.Name));
        }
    }

    private static void ProcessSheet(string sheetName, ISheetCells cells, bool singleRegion, ParseContext context, Output output, CancellationToken cancellationToken)
    {
        var scan = SheetScanner.Scan(cells, cancellationToken);
        if (scan.Bounds is not { } bounds)
        {
            return;
        }

        var options = context.Options;
        var small = Math.Max(options.SmallTableMaxCells, 0);
        var merges = new MergeIndex(scan.Merges);

        // Whole sheet is small: a form or a short list. Keep it as one table, blank cells and all.
        if (scan.GridCellCount <= small)
        {
            var whole = SheetReading.ReadRects(cells, scan, [bounds], cancellationToken)[0];
            var model = SmallTableBuilder.Build(whole, scan, merges, bounds, 0, null, small);
            output.AddTable(context, sheetName, bounds, model);
            return;
        }

        var regions = singleRegion ? RegionDetector.WholeSheet(scan) : RegionDetector.Detect(scan);
        var windowRows = HeaderDetector.WindowRows + Math.Max(options.TableSampleRows, 0);

        // Read everything the regions need (small regions completely, big ones only their top rows) in one pass.
        var rects = new List<CellRect>();
        var plans = new List<(DetectedRegion Region, int RectIndex, int FirstCaptionIndex)>();
        foreach (var region in regions)
        {
            var rect = region.Rect;
            var read = rect.Area <= small
                ? rect
                : rect with { Bottom = Math.Min(rect.Bottom, rect.Top + windowRows - 1) };
            plans.Add((region, rects.Count, rects.Count + 1));
            rects.Add(read);
            foreach (var (row, column) in region.Captions)
            {
                rects.Add(new CellRect(row, column, row, column));
            }
        }

        var data = SheetReading.ReadRects(cells, scan, rects, cancellationToken);
        foreach (var (region, rectIndex, firstCaption) in plans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProcessRegion(sheetName, region, data[rectIndex], data.AsSpan(firstCaption, region.Captions.Count), scan, merges, context, output, windowRows);
        }
    }

    private static void ProcessRegion(
        string sheetName,
        DetectedRegion region,
        RectData data,
        ReadOnlySpan<RectData> captionData,
        SheetScan scan,
        MergeIndex merges,
        ParseContext context,
        Output output,
        int windowRows)
    {
        var options = context.Options;
        var rect = region.Rect;
        var isSmall = rect.Area <= options.SmallTableMaxCells;

        var captions = new List<string>();
        foreach (var captionRect in captionData)
        {
            var cell = captionRect.Get(captionRect.Rect.Top, captionRect.Rect.Left);
            if (HeaderDetector.Normalize(cell.Text) is { } text)
            {
                captions.Add(text);
            }
        }

        var rowsInWindow = Math.Min(rect.RowCount, windowRows);
        var window = SheetReading.Slice(data, rect.Top, rowsInWindow, rect.Left, rect.ColumnCount);
        var windowMerges = merges.AnchoredIn(new CellRect(rect.Top, rect.Left, rect.Top + rowsInWindow - 1, rect.Right));
        var layout = HeaderDetector.Detect(window, rect.RowCount, rect.Top, rect.Left, windowMerges);
        if (layout.Caption is not null)
        {
            captions.Add(layout.Caption);
        }

        var caption = captions.Count == 0 ? null : string.Join(' ', captions);

        // Banner rows are not part of the table; neither are the columns only they used.
        var table = rect with { Top = rect.Top + layout.DecorativeRows };
        if (layout.DecorativeRows > 0)
        {
            table = TightenColumns(scan, table);
        }

        if (isSmall)
        {
            var model = SmallTableBuilder.Build(data, scan, merges, table, layout.HeaderRows, caption, options.SmallTableMaxCells);
            output.AddTable(context, sheetName, table, model);
            return;
        }

        var headerRows = layout.HeaderRows;
        var dataRowCount = table.RowCount - headerRows;
        var width = table.ColumnCount;
        var headerCells = SheetReading.Slice(data, table.Top, headerRows, table.Left, width);
        var headerMerges = merges.AnchoredIn(new CellRect(table.Top, table.Left, table.Top + Math.Max(headerRows, 1) - 1, table.Right));
        var columns = HeaderDetector.BuildColumnNames(headerCells, width, table.Top, table.Left, headerMerges);

        var sampleCount = Math.Min(Math.Max(options.TableSampleRows, 0), dataRowCount);
        var samples = new List<IReadOnlyList<string>>(sampleCount);
        for (var i = 0; i < sampleCount; i++)
        {
            var row = table.Top + headerRows + i;
            var values = new string[width];
            for (var c = 0; c < width; c++)
            {
                values[c] = Clean(data.Get(row, table.Left + c).Text);
            }

            samples.Add(values);
        }

        var range = table.ToA1();
        var description = Describe(context.FileName, sheetName, range, dataRowCount, caption, columns, samples);
        var key = sheetName + "!" + range;
        var accepted = output.AddSection(new DocumentSection(
            SectionKind.TableSummary,
            description,
            Location(context, sheetName, range),
            KeepWhole: true,
            TableKey: key));
        if (accepted)
        {
            output.Tables.Add(new SpreadsheetTable(key, sheetName, range, headerRows, columns, dataRowCount, samples, description));
        }
    }

    /// <summary>Narrows the rectangle to the columns the remaining rows really use.</summary>
    private static CellRect TightenColumns(SheetScan scan, CellRect rect)
    {
        int left = int.MaxValue, right = int.MinValue;
        for (var k = scan.LowerBound(rect.Top); k < scan.RowNumbers.Length && scan.RowNumbers[k] <= rect.Bottom; k++)
        {
            var runs = scan.Runs[k];
            left = Math.Min(left, runs[0]);
            right = Math.Max(right, runs[^1]);
        }

        return left > right ? rect : rect with { Left = Math.Max(left, rect.Left), Right = Math.Min(right, rect.Right) };
    }

    private static string Describe(string fileName, string sheet, string range, int dataRowCount, string? caption, IReadOnlyList<string> columns, List<IReadOnlyList<string>> samples)
    {
        var builder = new StringBuilder();
        builder.Append("檔案：").Append(fileName).Append('\n');
        builder.Append("工作表：").Append(sheet).Append("（範圍 ").Append(range).Append("，共 ").Append(dataRowCount).Append(" 筆資料）\n");
        if (!string.IsNullOrEmpty(caption))
        {
            builder.Append("表格標題：").Append(caption).Append('\n');
        }

        builder.Append("欄位：").Append(string.Join("、", columns)).Append('\n');
        builder.Append("範例資料：\n");
        foreach (var row in samples)
        {
            for (var c = 0; c < columns.Count; c++)
            {
                if (c > 0)
                {
                    builder.Append('；');
                }

                builder.Append(columns[c]).Append('=').Append(row[c]);
            }

            builder.Append('\n');
        }

        builder.Append("（完整內容請使用 query_table 查詢）");
        return builder.ToString();
    }

    /// <summary>One line, trimmed, and not longer than a sample value needs to be.</summary>
    private static string Clean(string? text)
    {
        var value = HeaderDetector.Normalize(text) ?? string.Empty;
        return value.Length > MaxSampleValueLength ? value[..MaxSampleValueLength] + "…" : value;
    }

    private static SourceLocation Location(ParseContext context, string sheet, string range) =>
        new() { Sheet = sheet, CellRange = range, Title = sheet, EmbeddedPath = context.EmbeddedPath };

    /// <summary>Collects the result and enforces <see cref="ParserOptions.MaxExtractedChars"/>.</summary>
    private sealed class Output(ParseContext context)
    {
        private long _chars;
        private bool _truncated;

        public List<DocumentSection> Sections { get; } = [];

        public List<EmbeddedFile> EmbeddedFiles { get; } = [];

        public List<ExtractedImage> Images { get; } = [];

        public List<SpreadsheetTable> Tables { get; } = [];

        public List<string> Warnings { get; } = [];

        public void AddTable(ParseContext ctx, string sheet, CellRect range, TableModel model)
        {
            if (model.Cells.Count == 0)
            {
                return;
            }

            _ = AddSection(new DocumentSection(
                SectionKind.Table,
                HtmlTableRenderer.Render(model),
                Location(ctx, sheet, range.ToA1()),
                KeepWhole: true));
        }

        public bool AddSection(DocumentSection section)
        {
            if (_truncated)
            {
                return false;
            }

            if (_chars + section.Text.Length > context.Options.MaxExtractedChars)
            {
                _truncated = true;
                Warnings.Add("truncated:max-extracted-chars");
                return false;
            }

            _chars += section.Text.Length;
            Sections.Add(section);
            return true;
        }

        public ParsedDocument ToDocument() => new(Sections, EmbeddedFiles, Images, Tables, Warnings);
    }
}
