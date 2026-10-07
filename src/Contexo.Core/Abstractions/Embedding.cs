namespace Contexo.Core.Abstractions;

/// <summary>Turns text into L2-normalised vectors. Implementations must be thread-safe and load the model lazily.</summary>
public interface IEmbeddingService
{
    /// <summary>Stable identifier stored with every vector, e.g. "bge-small-zh-v1.5/int8". Vectors from different ids are never compared.</summary>
    string ModelId { get; }

    int Dimensions { get; }

    /// <summary>True when the model files exist and can be loaded; false makes search fall back to keyword-only.</summary>
    bool IsAvailable { get; }

    /// <summary>Embeds passages for indexing (applies the model's passage prefix, if any). Batches internally.</summary>
    Task<IReadOnlyList<float[]>> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken);

    /// <summary>Embeds a search query (applies the model's query prefix/instruction, if any).</summary>
    Task<float[]> EmbedQueryAsync(string text, CancellationToken cancellationToken);
}
