using Contexo.Core.Abstractions;
using Contexo.Core.Parsing;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Spreadsheet = DocumentFormat.OpenXml.Spreadsheet;

namespace Contexo.Core.Tests.Parsing;

public sealed class OfficeEmbeddedContentTests
{
    // 1x1 transparent PNG.
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    private static byte[] CreateWorkbook()
    {
        using var stream = new MemoryStream();
        using (var workbook = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
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

        return stream.ToArray();
    }

    private static MemoryStream CreateDocx(byte[] workbook, bool includeOleObject)
    {
        var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new Document(new Body(new Paragraph(new Run(new Text("內文")))));

            var embedded = main.AddEmbeddedPackagePart("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
            using (var target = embedded.GetStream(FileMode.Create, FileAccess.Write))
            {
                target.Write(workbook);
            }

            var image = main.AddImagePart(ImagePartType.Png);
            using (var target = image.GetStream(FileMode.Create, FileAccess.Write))
            {
                target.Write(Png);
            }

            if (includeOleObject)
            {
                var ole = main.AddEmbeddedObjectPart("application/vnd.openxmlformats-officedocument.oleObject");
                using var target = ole.GetStream(FileMode.Create, FileAccess.Write);
                target.Write([0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]);
            }
        }

        stream.Position = 0;
        return stream;
    }

    [Fact]
    public void Embedded_xlsx_is_extracted_with_name_content_and_location()
    {
        var workbook = CreateWorkbook();
        var location = new SourceLocation { Page = 3 };
        using var stream = CreateDocx(workbook, includeOleObject: false);
        using var document = WordprocessingDocument.Open(stream, false);

        var files = OfficeEmbeddedContent.ExtractEmbeddedFiles(document.MainDocumentPart!, location);

        var file = Assert.Single(files);
        Assert.EndsWith(".xlsx", file.FileName);
        Assert.Equal(workbook, file.Content);
        Assert.Same(location, file.ContainerLocation);
    }

    [Fact]
    public void Embedded_ole_objects_are_skipped_without_error()
    {
        var workbook = CreateWorkbook();
        using var stream = CreateDocx(workbook, includeOleObject: true);
        using var document = WordprocessingDocument.Open(stream, false);

        var files = OfficeEmbeddedContent.ExtractEmbeddedFiles(document.MainDocumentPart!, SourceLocation.None);

        Assert.Single(files);
        Assert.All(files, f => Assert.EndsWith(".xlsx", f.FileName));
    }

    [Fact]
    public void Image_is_extracted_with_type_content_location_and_context()
    {
        using var stream = CreateDocx(CreateWorkbook(), includeOleObject: false);
        using var document = WordprocessingDocument.Open(stream, false);
        var location = new SourceLocation { Slide = 2 };

        var images = OfficeEmbeddedContent.ExtractImages(document.MainDocumentPart!, location, "季度報告");

        var image = Assert.Single(images);
        Assert.Equal("image/png", image.ContentType);
        Assert.Equal(Png, image.Content);
        Assert.EndsWith(".png", image.FileName);
        Assert.Same(location, image.Location);
        Assert.Equal("季度報告", image.ContextText);
    }

    [Fact]
    public void A_part_without_embedded_content_yields_nothing()
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            document.AddMainDocumentPart().Document = new Document(new Body());
        }

        stream.Position = 0;
        using var reopened = WordprocessingDocument.Open(stream, false);

        Assert.Empty(OfficeEmbeddedContent.ExtractEmbeddedFiles(reopened.MainDocumentPart!, SourceLocation.None));
        Assert.Empty(OfficeEmbeddedContent.ExtractImages(reopened.MainDocumentPart!, SourceLocation.None, null));
    }
}
