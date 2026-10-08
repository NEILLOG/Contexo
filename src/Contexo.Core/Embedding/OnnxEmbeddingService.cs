using Contexo.Core.Abstractions;

namespace Contexo.Core.Embedding;

/// <summary>Stub. Implemented by T03.</summary>
internal sealed class OnnxEmbeddingService : IEmbeddingService
{
    public string ModelId => throw new NotImplementedException("T03");

    public int Dimensions => throw new NotImplementedException("T03");

    public bool IsAvailable => throw new NotImplementedException("T03");

    public Task<IReadOnlyList<float[]>> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken) => throw new NotImplementedException("T03");

    public Task<float[]> EmbedQueryAsync(string text, CancellationToken cancellationToken) => throw new NotImplementedException("T03");
}
