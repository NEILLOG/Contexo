namespace Contexo.Core.Abstractions;

/// <param name="Ordinal">0-based order within the document (including embedded files, which follow their container).</param>
/// <param name="Text">Text shown to users and returned to AI clients.</param>
/// <param name="EmbeddingText">Text actually embedded: "{file title} › {heading path}\n{Text}". Never shown.</param>
/// <param name="TableKey">Copied from <see cref="DocumentSection.TableKey"/> for table summaries.</param>
public sealed record Chunk(
    int Ordinal,
    SectionKind Kind,
    string Text,
    string EmbeddingText,
    SourceLocation Location,
    string? TableKey = null);

public sealed record ChunkingOptions
{
    /// <summary>Target maximum characters per chunk (CJK characters count as 1).</summary>
    public int MaxChars { get; init; } = 500;
    /// <summary>Characters of overlap between consecutive chunks split from the same prose section.</summary>
    public int OverlapChars { get; init; } = 80;
    /// <summary>Prose shorter than this is merged with its neighbour inside the same heading.</summary>
    public int MinChars { get; init; } = 40;
    /// <summary>Absolute ceiling; even KeepWhole sections longer than this are split (tables at row boundaries, repeating the header).</summary>
    public int HardMaxChars { get; init; } = 4000;
}

public interface IChunker
{
    /// <param name="documentTitle">File name without extension; prefixed to every <see cref="Chunk.EmbeddingText"/>.</param>
    IReadOnlyList<Chunk> Split(string documentTitle, IReadOnlyList<DocumentSection> sections, ChunkingOptions options);
}
