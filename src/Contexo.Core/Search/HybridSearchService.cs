using Contexo.Core.Abstractions;

namespace Contexo.Core.Search;

/// <summary>Stub. Implemented by T11.</summary>
internal sealed class HybridSearchService : ISearchService
{
    public Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken) => throw new NotImplementedException("T11");
}
