using System.Text.Json;

namespace Contexo.Core.Embedding;

/// <summary>Raised when <c>contexo-model.json</c> is missing, unreadable or invalid. The message says what is wrong.</summary>
internal sealed class ModelManifestException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Names of the ONNX graph inputs.</summary>
internal sealed record ModelInputNames(string Ids, string Mask, string? TypeIds);

/// <summary>
/// Describes one embedding model folder (<c>contexo-model.json</c>), so changing the model never needs a code change.
/// </summary>
internal sealed record ModelManifest
{
    public const string FileName = "contexo-model.json";
    public const string PoolingCls = "cls";
    public const string PoolingMean = "mean";
    public const string TokenizerWordPiece = "wordpiece";

    public required string Id { get; init; }
    public required string Tokenizer { get; init; }
    public required string Vocab { get; init; }
    public bool Lowercase { get; init; } = true;
    public required int MaxTokens { get; init; }
    public required int Dimensions { get; init; }
    public required string Pooling { get; init; }
    public bool Normalize { get; init; } = true;
    public string QueryPrefix { get; init; } = "";
    public string PassagePrefix { get; init; } = "";
    public required ModelInputNames Inputs { get; init; }
    public required string Output { get; init; }

    /// <summary>File name of the ONNX model inside the model folder.</summary>
    public string ModelFile { get; init; } = "model.onnx";

    /// <summary>Reads and validates <c>contexo-model.json</c> inside <paramref name="modelDirectory"/>.</summary>
    public static ModelManifest Load(string modelDirectory)
    {
        var path = Path.Combine(modelDirectory, FileName);
        if (!File.Exists(path))
        {
            throw new ModelManifestException($"{FileName} not found in '{modelDirectory}'");
        }

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModelManifestException($"Cannot read {path}: {ex.Message}", ex);
        }

        return Parse(json);
    }

    /// <summary>Parses and validates manifest JSON.</summary>
    public static ModelManifest Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException ex)
        {
            throw new ModelManifestException($"{FileName} is not valid JSON: {ex.Message}", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ModelManifestException($"{FileName} must contain a JSON object");
            }

            var id = RequiredString(root, "id");
            var tokenizer = RequiredString(root, "tokenizer").ToLowerInvariant();
            var vocab = RequiredString(root, "vocab");
            var maxTokens = RequiredInt(root, "maxTokens");
            var dimensions = RequiredInt(root, "dimensions");
            var pooling = RequiredString(root, "pooling").ToLowerInvariant();
            var output = RequiredString(root, "output");

            if (pooling is not (PoolingCls or PoolingMean))
            {
                throw new ModelManifestException($"{FileName}: 'pooling' must be '{PoolingCls}' or '{PoolingMean}', got '{pooling}'");
            }

            if (maxTokens < 3)
            {
                throw new ModelManifestException($"{FileName}: 'maxTokens' must be at least 3, got {maxTokens}");
            }

            if (dimensions < 1)
            {
                throw new ModelManifestException($"{FileName}: 'dimensions' must be positive, got {dimensions}");
            }

            if (!root.TryGetProperty("inputs", out var inputs) || inputs.ValueKind != JsonValueKind.Object)
            {
                throw new ModelManifestException($"{FileName}: missing required object 'inputs'");
            }

            var inputNames = new ModelInputNames(
                RequiredString(inputs, "ids", "inputs.ids"),
                RequiredString(inputs, "mask", "inputs.mask"),
                OptionalString(inputs, "typeIds"));

            return new ModelManifest
            {
                Id = id,
                Tokenizer = tokenizer,
                Vocab = vocab,
                Lowercase = OptionalBool(root, "lowercase", true),
                MaxTokens = maxTokens,
                Dimensions = dimensions,
                Pooling = pooling,
                Normalize = OptionalBool(root, "normalize", true),
                QueryPrefix = OptionalString(root, "queryPrefix") ?? "",
                PassagePrefix = OptionalString(root, "passagePrefix") ?? "",
                Inputs = inputNames,
                Output = output,
                ModelFile = OptionalString(root, "model") ?? "model.onnx",
            };
        }
    }

    private static string RequiredString(JsonElement element, string name, string? display = null)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new ModelManifestException($"{FileName}: missing or empty required string '{display ?? name}'");
        }

        return value.GetString()!;
    }

    private static int RequiredInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
        {
            throw new ModelManifestException($"{FileName}: missing or non-integer required number '{name}'");
        }

        return number;
    }

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool OptionalBool(JsonElement element, string name, bool fallback) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;
}
