using System.Diagnostics;
using Contexo.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Contexo.Core.Search;

/// <summary>Semantic (vector) plus keyword (FTS5 trigram) retrieval, fused with Reciprocal Rank Fusion. Thread-safe.</summary>
internal sealed class HybridSearchService : ISearchService
{
    private const int RrfK = 60;
    private const int MaxHitsPerFile = 3;
    private const int MaxTopK = 200;

    private readonly IKnowledgeStore _store;
    private readonly IEmbeddingService _embedding;
    private readonly ILogger<HybridSearchService> _logger;
    private readonly VectorIndex _vectorIndex;

    public HybridSearchService(IKnowledgeStore store, IEmbeddingService embedding, ILogger<HybridSearchService> logger)
    {
        _store = store;
        _embedding = embedding;
        _logger = logger;
        _vectorIndex = new VectorIndex(store, logger);
    }

    public async Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Query) || request.TopK <= 0)
        {
            return new SearchResponse([], false);
        }

        var started = Stopwatch.GetTimestamp();
        var topK = Math.Min(request.TopK, MaxTopK);
        var prefixes = NormalizePrefixes(request.PathPrefixes);

        // Candidates are filtered by path afterwards, so fetch more when a filter is active.
        var candidateCount = Math.Max(topK * 4, 30) * (prefixes.Count > 0 ? 5 : 1);

        var keywordTask = KeywordAsync(request.Query, candidateCount, cancellationToken);
        var semanticTask = SemanticAsync(request.Query, candidateCount, cancellationToken);
        await Task.WhenAll(keywordTask, semanticTask);
        var keywordIds = keywordTask.Result;
        var (semanticIds, degraded) = semanticTask.Result;

        var fused = Fuse(semanticIds, keywordIds);
        var hits = await BuildHitsAsync(fused, prefixes, topK, cancellationToken);

        _logger.LogInformation(
            "Search finished in {Elapsed:F0} ms: {Semantic} semantic and {Keyword} keyword candidates, {Hits} hits, degraded={Degraded}",
            Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            semanticIds.Count,
            keywordIds.Count,
            hits.Count,
            degraded);
        return new SearchResponse(hits, degraded);
    }

    private async Task<IReadOnlyList<long>> KeywordAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var (fts, like) = KeywordQueryBuilder.Build(query);
        if (fts is null && like.Count == 0)
        {
            return [];
        }

        var hits = await _store.KeywordSearchAsync(fts, like, limit, cancellationToken);
        return hits.Select(h => h.ChunkId).ToList();
    }

    private async Task<(IReadOnlyList<long> Ids, bool Degraded)> SemanticAsync(string query, int limit, CancellationToken cancellationToken)
    {
        if (!_embedding.IsAvailable)
        {
            return ([], true);
        }

        try
        {
            var vector = await _embedding.EmbedQueryAsync(query, cancellationToken);
            var snapshot = await _vectorIndex.GetSnapshotAsync(_embedding.ModelId, vector.Length, cancellationToken);
            return (snapshot.Search(vector, limit), false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Semantic search failed; using keyword results only");
            return ([], true);
        }
    }

    /// <summary>Reciprocal Rank Fusion. Returns chunk ids best first, with the lists each id appeared in.</summary>
    private static List<(long ChunkId, double Score, MatchKinds Kinds)> Fuse(IReadOnlyList<long> semantic, IReadOnlyList<long> keyword)
    {
        var scores = new Dictionary<long, (double Score, MatchKinds Kinds, int SemanticRank)>();
        for (var i = 0; i < semantic.Count; i++)
        {
            scores[semantic[i]] = (1.0 / (RrfK + i + 1), MatchKinds.Semantic, i);
        }

        for (var i = 0; i < keyword.Count; i++)
        {
            var add = 1.0 / (RrfK + i + 1);
            scores[keyword[i]] = scores.TryGetValue(keyword[i], out var existing)
                ? (existing.Score + add, existing.Kinds | MatchKinds.Keyword, existing.SemanticRank)
                : (add, MatchKinds.Keyword, int.MaxValue);
        }

        return scores
            .OrderByDescending(p => p.Value.Score)
            .ThenBy(p => p.Value.SemanticRank)
            .ThenBy(p => p.Key)
            .Select(p => (p.Key, p.Value.Score, p.Value.Kinds))
            .ToList();
    }

    private async Task<IReadOnlyList<SearchHit>> BuildHitsAsync(
        List<(long ChunkId, double Score, MatchKinds Kinds)> fused,
        IReadOnlyList<string> prefixes,
        int topK,
        CancellationToken cancellationToken)
    {
        if (fused.Count == 0)
        {
            return [];
        }

        var details = (await _store.GetChunksAsync(fused.Select(f => f.ChunkId).ToList(), cancellationToken))
            .ToDictionary(d => d.ChunkId);

        var perFile = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var hits = new List<SearchHit>(topK);
        double topScore = 0;
        foreach (var (chunkId, score, kinds) in fused)
        {
            if (hits.Count >= topK)
            {
                break;
            }

            if (!details.TryGetValue(chunkId, out var detail) || !MatchesPrefix(detail.FilePath, prefixes))
            {
                continue;
            }

            perFile.TryGetValue(detail.FilePath, out var count);
            if (count >= MaxHitsPerFile)
            {
                continue;
            }

            perFile[detail.FilePath] = count + 1;
            if (hits.Count == 0)
            {
                topScore = score;
            }

            hits.Add(new SearchHit(
                detail.ChunkId,
                detail.FilePath,
                FileNameOf(detail.FilePath),
                detail.Kind,
                detail.Text,
                detail.Location,
                topScore > 0 ? score / topScore : 0,
                kinds,
                detail.TableId));
        }

        return hits;
    }

    private static string FileNameOf(string path)
    {
        var index = path.LastIndexOfAny(['\\', '/']);
        return index < 0 ? path : path[(index + 1)..];
    }

    private static List<string> NormalizePrefixes(IReadOnlyList<string>? prefixes)
    {
        var result = new List<string>();
        if (prefixes is null)
        {
            return result;
        }

        foreach (var prefix in prefixes)
        {
            var normalized = NormalizePath(prefix);
            if (normalized.Length > 0)
            {
                result.Add(normalized);
            }
        }

        return result;
    }

    private static string NormalizePath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? "" : path.Trim().Replace('\\', '/').TrimEnd('/');

    /// <summary>Case-insensitive, on directory boundaries: "C:/A/報價" matches "C:/A/報價/x.docx" but not "C:/A/報價單/x.docx".</summary>
    private static bool MatchesPrefix(string filePath, IReadOnlyList<string> prefixes)
    {
        if (prefixes.Count == 0)
        {
            return true;
        }

        var path = NormalizePath(filePath);
        foreach (var prefix in prefixes)
        {
            if (path.Length > prefix.Length
                && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && path[prefix.Length] == '/')
            {
                return true;
            }

            if (path.Equals(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
