using Contexo.Core.Abstractions;
using DocumentFormat.OpenXml.Packaging;

namespace Contexo.Core.Parsing;

/// <summary>Helpers shared by the Word and PowerPoint parsers for content embedded in an Open XML part.</summary>
internal static class OfficeEmbeddedContent
{
    private static readonly Dictionary<string, string> ExtensionByContentType = new(StringComparer.OrdinalIgnoreCase)
    {
        ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"] = ".docx",
        ["application/vnd.ms-word.document.macroEnabled.12"] = ".docm",
        ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"] = ".xlsx",
        ["application/vnd.ms-excel.sheet.macroEnabled.12"] = ".xlsm",
        ["application/vnd.openxmlformats-officedocument.presentationml.presentation"] = ".pptx",
        ["application/vnd.ms-powerpoint.presentation.macroEnabled.12"] = ".pptm",
    };

    /// <summary>
    /// Reads the <see cref="EmbeddedPackagePart"/>s (embedded docx / xlsx / pptx) that hang directly off <paramref name="part"/>.
    /// OLE compound files (<see cref="EmbeddedObjectPart"/>, e.g. old .xls or Visio) are skipped in version 1.
    /// </summary>
    public static IReadOnlyList<EmbeddedFile> ExtractEmbeddedFiles(OpenXmlPart part, SourceLocation location)
    {
        ArgumentNullException.ThrowIfNull(part);
        ArgumentNullException.ThrowIfNull(location);

        var result = new List<EmbeddedFile>();
        var seen = new HashSet<Uri>();
        foreach (var embedded in part.GetPartsOfType<EmbeddedPackagePart>())
        {
            if (!seen.Add(embedded.Uri))
            {
                continue;
            }

            var extension = ResolveExtension(embedded);
            if (extension is null)
            {
                continue;
            }

            var name = Path.GetFileNameWithoutExtension(embedded.Uri.OriginalString);
            if (string.IsNullOrEmpty(name))
            {
                name = "embedded" + (result.Count + 1);
            }

            result.Add(new EmbeddedFile(name + extension, ReadAll(embedded), location));
        }

        return result;
    }

    /// <summary>Reads the <see cref="ImagePart"/>s that hang directly off <paramref name="part"/>. Each image is returned once even when referenced several times.</summary>
    /// <param name="contextText">Nearby text (slide title, paragraph) stored with each image for later vision-model use.</param>
    public static IReadOnlyList<ExtractedImage> ExtractImages(OpenXmlPart part, SourceLocation location, string? contextText)
    {
        ArgumentNullException.ThrowIfNull(part);
        ArgumentNullException.ThrowIfNull(location);

        var result = new List<ExtractedImage>();
        var seen = new HashSet<Uri>();
        foreach (var image in part.GetPartsOfType<ImagePart>())
        {
            if (!seen.Add(image.Uri))
            {
                continue;
            }

            var fileName = Path.GetFileName(image.Uri.OriginalString);
            result.Add(new ExtractedImage(fileName, image.ContentType, ReadAll(image), location, contextText));
        }

        return result;
    }

    private static string? ResolveExtension(EmbeddedPackagePart part)
    {
        if (ExtensionByContentType.TryGetValue(part.ContentType, out var known))
        {
            return known;
        }

        var fromUri = Path.GetExtension(part.Uri.OriginalString);
        return string.IsNullOrEmpty(fromUri) ? null : fromUri.ToLowerInvariant();
    }

    private static byte[] ReadAll(OpenXmlPart part)
    {
        using var stream = part.GetStream(FileMode.Open, FileAccess.Read);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
