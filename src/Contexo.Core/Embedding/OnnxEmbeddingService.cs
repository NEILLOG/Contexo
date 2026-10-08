using Contexo.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;

namespace Contexo.Core.Embedding;

/// <summary>
/// Local embedding with ONNX Runtime (CPU). The model folder is described by <c>contexo-model.json</c>, so models can be
/// swapped without code changes. The session is created lazily on the first Embed call; <see cref="IsAvailable"/> only checks files.
/// </summary>
internal sealed class OnnxEmbeddingService : IEmbeddingService, IDisposable
{
    internal const int MaxBatchSize = 16;
    internal const string DefaultMarkerFileName = "default";
    private const long NegativeCacheMilliseconds = 10_000;

    private readonly Func<string?> _modelsDirectoryProvider;
    private readonly ILogger<OnnxEmbeddingService> _logger;
    private readonly object _resolveLock = new();
    private Lazy<Runtime>? _runtime;
    private ModelCandidate? _candidate;
    private long _retryNotBefore;
    private volatile bool _loadFailed;
    private bool _disposed;

    public OnnxEmbeddingService(IAppPaths paths, ILogger<OnnxEmbeddingService> logger)
        : this(() => paths.ModelsDirectory, logger)
    {
    }

    /// <summary>Test / tooling entry point: <paramref name="modelsDirectory"/> is the folder that contains model folders.</summary>
    internal OnnxEmbeddingService(string modelsDirectory, ILogger<OnnxEmbeddingService> logger)
        : this(() => modelsDirectory, logger)
    {
    }

    private OnnxEmbeddingService(Func<string?> modelsDirectoryProvider, ILogger<OnnxEmbeddingService> logger)
    {
        _modelsDirectoryProvider = modelsDirectoryProvider;
        _logger = logger;
    }

    public string ModelId => Resolve()?.Manifest.Id ?? "";

    public int Dimensions => Resolve()?.Manifest.Dimensions ?? 0;

    public bool IsAvailable => !_loadFailed && Resolve() is not null;

    public Task<IReadOnlyList<float[]>> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var runtime = GetRuntime();
        return EmbedAsync(runtime, texts, runtime.Manifest.PassagePrefix, cancellationToken);
    }

    public async Task<float[]> EmbedQueryAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        var runtime = GetRuntime();
        var result = await EmbedAsync(runtime, [text], runtime.Manifest.QueryPrefix, cancellationToken).ConfigureAwait(false);
        return result[0];
    }

    public void Dispose()
    {
        lock (_resolveLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_runtime is { IsValueCreated: true } created && !_loadFailed)
            {
                created.Value.Session.Dispose();
            }
        }
    }

    /// <summary>Finds the model folder and validates its files. Returns null (and logs why) when no usable model exists.</summary>
    private ModelCandidate? Resolve()
    {
        lock (_resolveLock)
        {
            if (_candidate is not null)
            {
                return _candidate;
            }

            // A missing model is re-checked now and then (the user may download it later) but not on every call.
            if (Environment.TickCount64 < _retryNotBefore)
            {
                return null;
            }

            try
            {
                _candidate = FindModel();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "Embedding model lookup failed");
                _candidate = null;
            }

            _retryNotBefore = _candidate is null ? Environment.TickCount64 + NegativeCacheMilliseconds : 0;
            return _candidate;
        }
    }

    private ModelCandidate? FindModel()
    {
        var modelsDirectory = _modelsDirectoryProvider();
        if (string.IsNullOrWhiteSpace(modelsDirectory) || !Directory.Exists(modelsDirectory))
        {
            return null;
        }

        string? preferred = null;
        var markerPath = Path.Combine(modelsDirectory, DefaultMarkerFileName);
        if (File.Exists(markerPath))
        {
            preferred = File.ReadAllText(markerPath).Trim();
        }

        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(preferred))
        {
            candidates.Add(Path.Combine(modelsDirectory, preferred));
        }

        candidates.AddRange(Directory.EnumerateDirectories(modelsDirectory).Order(StringComparer.OrdinalIgnoreCase));

        foreach (var directory in candidates)
        {
            if (!File.Exists(Path.Combine(directory, ModelManifest.FileName)))
            {
                continue;
            }

            ModelManifest manifest;
            try
            {
                manifest = ModelManifest.Load(directory);
            }
            catch (ModelManifestException ex)
            {
                _logger.LogError("Embedding model manifest in {Folder} is invalid: {Reason}", Path.GetFileName(directory), ex.Message);
                return null;
            }

            if (manifest.Tokenizer != ModelManifest.TokenizerWordPiece)
            {
                _logger.LogError("Embedding model {ModelId} needs unsupported tokenizer '{Tokenizer}'", manifest.Id, manifest.Tokenizer);
                return null;
            }

            var modelPath = Path.Combine(directory, manifest.ModelFile);
            var vocabPath = Path.Combine(directory, manifest.Vocab);
            if (!File.Exists(modelPath) || !File.Exists(vocabPath))
            {
                _logger.LogWarning("Embedding model {ModelId} is incomplete (model file present: {HasModel}, vocab present: {HasVocab})",
                    manifest.Id, File.Exists(modelPath), File.Exists(vocabPath));
                return null;
            }

            return new ModelCandidate(manifest, modelPath, vocabPath);
        }

        return null;
    }

    private Runtime GetRuntime()
    {
        var candidate = Resolve() ?? throw new InvalidOperationException("Embedding model is not available");
        Lazy<Runtime> lazy;
        lock (_resolveLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _runtime ??= new Lazy<Runtime>(() => CreateRuntime(candidate), LazyThreadSafetyMode.ExecutionAndPublication);
            lazy = _runtime;
        }

        try
        {
            return lazy.Value;
        }
        catch (Exception ex)
        {
            _loadFailed = true;
            _logger.LogError(ex, "Embedding model {ModelId} failed to load", candidate.Manifest.Id);
            throw new InvalidOperationException("Embedding model is not available", ex);
        }
    }

    private Runtime CreateRuntime(ModelCandidate candidate)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var manifest = candidate.Manifest;
        var vocab = WordPieceTokenizer.LoadVocab(candidate.VocabPath);
        var tokenizer = new WordPieceTokenizer(vocab, manifest.Lowercase, manifest.MaxTokens);

        using var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2),
        };

        var session = new InferenceSession(candidate.ModelPath, options);
        try
        {
            ValidateGraph(session, manifest);
        }
        catch
        {
            session.Dispose();
            throw;
        }

        var supplyTypeIds = manifest.Inputs.TypeIds is not null && session.InputMetadata.ContainsKey(manifest.Inputs.TypeIds);
        _logger.LogInformation("Embedding model {ModelId} loaded in {ElapsedMs} ms ({Vocab} vocabulary entries)",
            manifest.Id, (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, vocab.Count);
        return new Runtime(manifest, tokenizer, session, supplyTypeIds);
    }

    private static void ValidateGraph(InferenceSession session, ModelManifest manifest)
    {
        foreach (var name in new[] { manifest.Inputs.Ids, manifest.Inputs.Mask })
        {
            if (!session.InputMetadata.ContainsKey(name))
            {
                throw new InvalidDataException($"Model has no input named '{name}'");
            }
        }

        if (!session.OutputMetadata.ContainsKey(manifest.Output))
        {
            throw new InvalidDataException($"Model has no output named '{manifest.Output}'");
        }
    }

    private static Task<IReadOnlyList<float[]>> EmbedAsync(Runtime runtime, IReadOnlyList<string> texts, string prefix, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (texts.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<float[]>>([]);
        }

        // Inference is CPU bound and synchronous; keep it off the caller's context.
        return Task.Run<IReadOnlyList<float[]>>(() => EmbedCore(runtime, texts, prefix, cancellationToken), cancellationToken);
    }

    private static float[][] EmbedCore(Runtime runtime, IReadOnlyList<string> texts, string prefix, CancellationToken cancellationToken)
    {
        var encoded = new int[texts.Count][];
        for (var i = 0; i < texts.Count; i++)
        {
            encoded[i] = runtime.Tokenizer.Encode(prefix.Length == 0 ? texts[i] : prefix + texts[i]);
        }

        // Group similar lengths together so padding stays small, then restore the caller's order.
        var order = Enumerable.Range(0, texts.Count).OrderBy(i => encoded[i].Length).ToArray();
        var results = new float[texts.Count][];
        for (var offset = 0; offset < order.Length; offset += MaxBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = order.AsSpan(offset, Math.Min(MaxBatchSize, order.Length - offset));
            RunBatch(runtime, encoded, batch, results);
        }

        return results;
    }

    private static void RunBatch(Runtime runtime, int[][] encoded, ReadOnlySpan<int> batch, float[][] results)
    {
        var manifest = runtime.Manifest;
        var count = batch.Length;
        var seq = 0;
        foreach (var index in batch)
        {
            seq = Math.Max(seq, encoded[index].Length);
        }

        var ids = new long[count * seq];
        var mask = new long[count * seq];
        var typeIds = runtime.SupplyTypeIds ? new long[count * seq] : null;
        for (var row = 0; row < count; row++)
        {
            var tokens = encoded[batch[row]];
            for (var col = 0; col < tokens.Length; col++)
            {
                ids[row * seq + col] = tokens[col];
                mask[row * seq + col] = 1;
            }
        }

        long[] shape = [count, seq];
        var inputNames = new List<string> { manifest.Inputs.Ids, manifest.Inputs.Mask };
        var inputValues = new List<OrtValue>
        {
            OrtValue.CreateTensorValueFromMemory(ids, shape),
            OrtValue.CreateTensorValueFromMemory(mask, shape),
        };
        if (typeIds is not null)
        {
            inputNames.Add(manifest.Inputs.TypeIds!);
            inputValues.Add(OrtValue.CreateTensorValueFromMemory(typeIds, shape));
        }

        try
        {
            using var runOptions = new RunOptions();
            using var outputs = runtime.Session.Run(runOptions, inputNames, inputValues, [manifest.Output]);
            var hidden = outputs[0].GetTensorDataAsSpan<float>();
            var dimensions = manifest.Dimensions;
            if (hidden.Length != count * seq * dimensions)
            {
                throw new InvalidDataException(
                    $"Model output has {hidden.Length} values, expected {count * seq * dimensions} ({count}x{seq}x{dimensions}); check 'dimensions' in {ModelManifest.FileName}");
            }

            for (var row = 0; row < count; row++)
            {
                var rowHidden = hidden.Slice(row * seq * dimensions, seq * dimensions);
                var length = encoded[batch[row]].Length;
                results[batch[row]] = Pool(rowHidden, length, dimensions, manifest);
            }
        }
        finally
        {
            foreach (var value in inputValues)
            {
                value.Dispose();
            }
        }
    }

    private static float[] Pool(ReadOnlySpan<float> hidden, int length, int dimensions, ModelManifest manifest)
    {
        var vector = new float[dimensions];
        if (manifest.Pooling == ModelManifest.PoolingCls)
        {
            hidden[..dimensions].CopyTo(vector);
        }
        else
        {
            // Mean over real tokens (padding has attention mask 0 and is excluded).
            for (var t = 0; t < length; t++)
            {
                var token = hidden.Slice(t * dimensions, dimensions);
                for (var d = 0; d < dimensions; d++)
                {
                    vector[d] += token[d];
                }
            }

            for (var d = 0; d < dimensions; d++)
            {
                vector[d] /= length;
            }
        }

        if (manifest.Normalize)
        {
            var sum = 0d;
            foreach (var value in vector)
            {
                sum += (double)value * value;
            }

            var norm = Math.Sqrt(sum);
            if (norm > 0)
            {
                var scale = (float)(1d / norm);
                for (var d = 0; d < dimensions; d++)
                {
                    vector[d] *= scale;
                }
            }
        }

        return vector;
    }

    private sealed record ModelCandidate(ModelManifest Manifest, string ModelPath, string VocabPath);

    private sealed record Runtime(ModelManifest Manifest, WordPieceTokenizer Tokenizer, InferenceSession Session, bool SupplyTypeIds);
}
