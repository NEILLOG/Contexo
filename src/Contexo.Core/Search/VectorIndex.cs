using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Contexo.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Contexo.Core.Search;

/// <summary>An immutable in-memory copy of every vector of one model: N x Dimensions floats plus the chunk ids.</summary>
internal sealed class VectorSnapshot
{
    public VectorSnapshot(string modelId, long version, int dimensions, float[] data, long[] chunkIds)
    {
        if (dimensions <= 0 || data.Length != chunkIds.Length * dimensions)
        {
            throw new ArgumentException("Vector data does not match the chunk ids and dimensions.");
        }

        ModelId = modelId;
        Version = version;
        Dimensions = dimensions;
        Data = data;
        ChunkIds = chunkIds;
    }

    public string ModelId { get; }

    public long Version { get; }

    public int Dimensions { get; }

    public float[] Data { get; }

    public long[] ChunkIds { get; }

    public int Count => ChunkIds.Length;

    /// <summary>Chunk ids of the <paramref name="k"/> most similar vectors, best first. Vectors are normalised, so the inner product is the cosine.</summary>
    public IReadOnlyList<long> Search(ReadOnlySpan<float> query, int k)
    {
        if (query.Length != Dimensions)
        {
            throw new ArgumentException("Query vector has the wrong number of dimensions.", nameof(query));
        }

        if (k <= 0 || Count == 0)
        {
            return [];
        }

        // Min-heap of size k: the root is the weakest of the current best k.
        var heap = new PriorityQueue<int, float>(Math.Min(k, Count) + 1);
        var data = Data.AsSpan();
        for (var row = 0; row < Count; row++)
        {
            var score = Dot(query, data.Slice(row * Dimensions, Dimensions));
            if (heap.Count < k)
            {
                heap.Enqueue(row, score);
            }
            else if (heap.TryPeek(out _, out var weakest) && score > weakest)
            {
                heap.DequeueEnqueue(row, score);
            }
        }

        var result = new long[heap.Count];
        for (var i = result.Length - 1; i >= 0; i--)
        {
            heap.TryDequeue(out var row, out _);
            result[i] = ChunkIds[row];
        }

        return result;
    }

    internal static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var length = Math.Min(a.Length, b.Length);
        var sum = 0f;
        var i = 0;
        if (Vector.IsHardwareAccelerated && length >= Vector<float>.Count)
        {
            var va = MemoryMarshal.Cast<float, Vector<float>>(a[..length]);
            var vb = MemoryMarshal.Cast<float, Vector<float>>(b[..length]);
            var acc = Vector<float>.Zero;
            for (var v = 0; v < va.Length; v++)
            {
                acc += va[v] * vb[v];
            }

            sum = Vector.Sum(acc);
            i = va.Length * Vector<float>.Count;
        }

        for (; i < length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }
}

/// <summary>
/// Cache of the vectors of the current embedding model. Reloaded when the store's index version or the model id changes;
/// while one search reloads, concurrent searches keep using the previous snapshot.
/// </summary>
internal sealed class VectorIndex(IKnowledgeStore store, ILogger logger)
{
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private volatile VectorSnapshot? _snapshot;

    public async Task<VectorSnapshot> GetSnapshotAsync(string modelId, int dimensions, CancellationToken cancellationToken)
    {
        var version = await store.GetIndexVersionAsync(cancellationToken);
        var current = _snapshot;
        if (IsFresh(current, modelId, dimensions, version))
        {
            return current!;
        }

        if (current is not null && current.ModelId == modelId && current.Dimensions == dimensions)
        {
            // Stale but usable: the first searcher reloads, the others carry on with the old copy.
            if (!await _loadLock.WaitAsync(0, cancellationToken))
            {
                return current;
            }
        }
        else
        {
            await _loadLock.WaitAsync(cancellationToken);
        }

        try
        {
            current = _snapshot;
            if (IsFresh(current, modelId, dimensions, version))
            {
                return current!;
            }

            var loaded = await LoadAsync(modelId, dimensions, version, cancellationToken);
            _snapshot = loaded;
            return loaded;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private static bool IsFresh(VectorSnapshot? snapshot, string modelId, int dimensions, long version) =>
        snapshot is not null && snapshot.Version == version && snapshot.ModelId == modelId && snapshot.Dimensions == dimensions;

    private async Task<VectorSnapshot> LoadAsync(string modelId, int dimensions, long version, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var rows = new List<float[]>();
        var ids = new List<long>();
        var skipped = 0;
        await foreach (var stored in store.ReadVectorsAsync(modelId, cancellationToken))
        {
            if (stored.Vector.Length != dimensions)
            {
                skipped++;
                continue;
            }

            rows.Add(stored.Vector);
            ids.Add(stored.ChunkId);
        }

        var data = new float[rows.Count * dimensions];
        for (var i = 0; i < rows.Count; i++)
        {
            rows[i].CopyTo(data, i * dimensions);
        }

        if (skipped > 0)
        {
            logger.LogWarning("Ignored {Skipped} stored vectors whose size does not match the model", skipped);
        }

        logger.LogInformation(
            "Vector index loaded: {Count} vectors in {Elapsed:F0} ms",
            ids.Count,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return new VectorSnapshot(modelId, version, dimensions, data, [.. ids]);
    }
}
