using Contexo.Core.Abstractions;

namespace Contexo.Core.Chunking;

/// <summary>Stub. Implemented by T09.</summary>
internal sealed class StructuredChunker : IChunker
{
    public IReadOnlyList<Chunk> Split(string documentTitle, IReadOnlyList<DocumentSection> sections, ChunkingOptions options) => throw new NotImplementedException("T09");
}
