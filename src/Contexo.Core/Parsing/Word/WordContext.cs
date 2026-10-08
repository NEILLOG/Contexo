using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Contexo.Core.Parsing.Word;

/// <summary>State shared by all text extractors of one Word document: styles, notes and image / embedded file references.</summary>
internal sealed class WordContext
{
    public const string RelationshipNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private readonly Dictionary<string, string> _keyByRelationshipId = new(StringComparer.Ordinal);
    private readonly Dictionary<long, Footnote> _footnotes = [];
    private readonly Dictionary<long, Endnote> _endnotes = [];
    private readonly Dictionary<(bool IsEndnote, long Id), string> _noteText = [];

    public WordContext(MainDocumentPart main)
    {
        ArgumentNullException.ThrowIfNull(main);

        Main = main;
        Styles = new WordStyleResolver(main);

        foreach (var pair in main.Parts)
        {
            var key = ReferenceKey(pair.OpenXmlPart);
            if (key is not null)
            {
                _keyByRelationshipId[pair.RelationshipId] = key;
            }
        }

        foreach (var footnote in main.FootnotesPart?.Footnotes?.Elements<Footnote>() ?? [])
        {
            if (footnote.Id?.Value is { } id && footnote.Type is null)
            {
                _footnotes[id] = footnote;
            }
        }

        foreach (var endnote in main.EndnotesPart?.Endnotes?.Elements<Endnote>() ?? [])
        {
            if (endnote.Id?.Value is { } id && endnote.Type is null)
            {
                _endnotes[id] = endnote;
            }
        }
    }

    public MainDocumentPart Main { get; }

    public WordStyleResolver Styles { get; }

    /// <summary>Image / embedded file keys referenced since the last <see cref="TakePendingReferences"/>.</summary>
    public List<string> PendingReferences { get; } = [];

    public void AddReference(string relationshipId)
    {
        if (_keyByRelationshipId.TryGetValue(relationshipId, out var key))
        {
            PendingReferences.Add(key);
        }
    }

    public List<string> TakePendingReferences()
    {
        var taken = new List<string>(PendingReferences);
        PendingReferences.Clear();
        return taken;
    }

    /// <summary>Plain text (paragraphs joined by a space) of a footnote or endnote, or null when it is unknown or empty.</summary>
    public string? GetNoteText(bool isEndnote, long id)
    {
        if (_noteText.TryGetValue((isEndnote, id), out var cached))
        {
            return cached;
        }

        OpenXmlElement? note = null;
        if (isEndnote)
        {
            if (_endnotes.TryGetValue(id, out var endnote))
            {
                note = endnote;
            }
        }
        else if (_footnotes.TryGetValue(id, out var footnote))
        {
            note = footnote;
        }

        var text = note is null
            ? string.Empty
            : string.Join(' ', new WordTextExtractor(this).ReadLines(note)).Trim();
        _noteText[(isEndnote, id)] = text;
        return text;
    }

    /// <summary>Key shared by image / embedded package parts and the lookup that maps extracted files back to where they were referenced.</summary>
    public static string? ReferenceKey(OpenXmlPart part) => part switch
    {
        ImagePart => ImageKey(Path.GetFileName(part.Uri.OriginalString)),
        EmbeddedPackagePart => EmbeddedKey(Path.GetFileNameWithoutExtension(part.Uri.OriginalString)),
        _ => null,
    };

    public static string ImageKey(string fileName) => "img:" + fileName;

    public static string EmbeddedKey(string nameWithoutExtension) => "emb:" + nameWithoutExtension;
}
