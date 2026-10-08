using System.Text;
using System.Xml;
using Contexo.Core.Abstractions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace Contexo.Core.Parsing.Spreadsheet;

/// <summary>One worksheet of an opened workbook.</summary>
internal sealed record WorkbookSheet(string Name, bool Hidden, WorksheetPart Part);

/// <summary>An opened .xlsx / .xlsm package with the workbook-wide data every sheet needs (sheet list, styles, shared strings).</summary>
internal sealed class XlsxWorkbook : IDisposable
{
    private static readonly byte[] CompoundFileSignature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
    private static readonly byte[] EncryptedPackageName = Encoding.Unicode.GetBytes("EncryptedPackage");

    private readonly SpreadsheetDocument _document;
    private readonly WorkbookPart _workbookPart;
    private string[]? _sharedStrings;

    private XlsxWorkbook(SpreadsheetDocument document, WorkbookPart workbookPart)
    {
        _document = document;
        _workbookPart = workbookPart;
        Styles = XlsxStyles.Load(workbookPart);
        Date1904 = workbookPart.Workbook?.WorkbookProperties?.Date1904?.Value == true;
        Sheets = ReadSheets(workbookPart);
    }

    public bool Date1904 { get; }

    public XlsxStyles Styles { get; }

    /// <summary>Worksheets in the order of workbook.xml. Chart sheets are not listed.</summary>
    public IReadOnlyList<WorkbookSheet> Sheets { get; }

    public string[] SharedStrings => _sharedStrings ??= LoadSharedStrings(_workbookPart);

    /// <summary>
    /// Opens a package. Password-protected files (an OLE compound file wrapping an encrypted package) raise
    /// <see cref="DocumentErrorCode.PasswordProtected"/>; anything else that is not a valid package raises <see cref="DocumentErrorCode.Corrupted"/>.
    /// </summary>
    public static XlsxWorkbook Open(Stream stream)
    {
        if (stream.CanSeek)
        {
            stream.Position = 0;
        }

        CheckContainer(stream);

        SpreadsheetDocument document;
        try
        {
            document = SpreadsheetDocument.Open(stream, false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new DocumentParseException(DocumentErrorCode.Corrupted, "無法開啟試算表檔案。", ex);
        }

        try
        {
            var workbookPart = document.WorkbookPart
                               ?? throw new DocumentParseException(DocumentErrorCode.Corrupted, "試算表缺少工作簿。");
            return new XlsxWorkbook(document, workbookPart);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            document.Dispose();
            if (ex is DocumentParseException)
            {
                throw;
            }

            throw new DocumentParseException(DocumentErrorCode.Corrupted, "無法讀取試算表內容。", ex);
        }
    }

    public void Dispose() => _document.Dispose();

    private static void CheckContainer(Stream stream)
    {
        Span<byte> header = stackalloc byte[8];
        var read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        if (stream.CanSeek)
        {
            stream.Position = 0;
        }

        if (read >= 2 && header[0] == (byte)'P' && header[1] == (byte)'K')
        {
            return;
        }

        if (read == header.Length && header.SequenceEqual(CompoundFileSignature))
        {
            if (ContainsEncryptedPackage(stream))
            {
                throw new DocumentParseException(DocumentErrorCode.PasswordProtected, "檔案有設定密碼。");
            }

            throw new DocumentParseException(DocumentErrorCode.Corrupted, "檔案不是新版試算表格式。");
        }

        throw new DocumentParseException(DocumentErrorCode.Corrupted, "檔案不是有效的試算表。");
    }

    private static bool ContainsEncryptedPackage(Stream stream)
    {
        if (!stream.CanSeek)
        {
            return false;
        }

        var overlap = EncryptedPackageName.Length - 1;
        var buffer = new byte[(1 << 20) + overlap];
        var carried = 0;
        try
        {
            while (true)
            {
                var read = stream.Read(buffer, carried, buffer.Length - carried);
                if (read <= 0)
                {
                    return false;
                }

                var total = carried + read;
                if (buffer.AsSpan(0, total).IndexOf(EncryptedPackageName) >= 0)
                {
                    return true;
                }

                carried = Math.Min(overlap, total);
                Buffer.BlockCopy(buffer, total - carried, buffer, 0, carried);
            }
        }
        finally
        {
            stream.Position = 0;
        }
    }

    private static List<WorkbookSheet> ReadSheets(WorkbookPart workbookPart)
    {
        var result = new List<WorkbookSheet>();
        var sheets = workbookPart.Workbook?.Sheets;
        if (sheets is null)
        {
            return result;
        }

        foreach (var sheet in sheets.Elements<Sheet>())
        {
            var id = sheet.Id?.Value;
            if (string.IsNullOrEmpty(id) || !workbookPart.TryGetPartById(id, out var part) || part is not WorksheetPart worksheetPart)
            {
                continue;
            }

            var state = sheet.State?.Value;
            var hidden = state == SheetStateValues.Hidden || state == SheetStateValues.VeryHidden;
            result.Add(new WorkbookSheet(sheet.Name?.Value ?? string.Empty, hidden, worksheetPart));
        }

        return result;
    }

    private static string[] LoadSharedStrings(WorkbookPart workbookPart)
    {
        var part = workbookPart.SharedStringTablePart;
        if (part is null)
        {
            return [];
        }

        var result = new List<string>();
        using var stream = part.GetStream(FileMode.Open, FileAccess.Read);
        using var reader = XmlReader.Create(stream, XlsxSheetCells.ReaderSettings);
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "si")
            {
                result.Add(XlsxSheetCells.ReadRichText(reader));
            }
        }

        return [.. result];
    }
}
