using System.Diagnostics;
using System.Text.Json;
using Contexo.Core.Embedding;
using Contexo.Core.Tests.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Contexo.Core.Tests.Embedding;

public sealed class OnnxEmbeddingServiceTests
{
    private const string ManifestJson = """
        {
          "id": "test/model", "tokenizer": "wordpiece", "vocab": "vocab.txt", "maxTokens": 32, "dimensions": 4, "pooling": "cls",
          "inputs": { "ids": "input_ids", "mask": "attention_mask" }, "output": "out"
        }
        """;

    private static OnnxEmbeddingService Create(string modelsDirectory, ILogger<OnnxEmbeddingService>? logger = null) =>
        new(modelsDirectory, logger ?? NullLogger<OnnxEmbeddingService>.Instance);

    private static void WriteModel(string modelsDirectory, string name, string manifestJson = ManifestJson, bool withModel = true, bool withVocab = true)
    {
        var folder = Path.Combine(modelsDirectory, name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, ModelManifest.FileName), manifestJson);
        if (withModel)
        {
            File.WriteAllBytes(Path.Combine(folder, "model.onnx"), [0, 1, 2]);
        }

        if (withVocab)
        {
            File.WriteAllText(Path.Combine(folder, "vocab.txt"), "[PAD]\n[UNK]\n[CLS]\n[SEP]\n");
        }
    }

    [Fact]
    public void Missing_models_directory_is_not_available_and_construction_does_not_throw()
    {
        using var temp = new TempDirectory();

        using var service = Create(temp.Combine("does-not-exist"));

        Assert.False(service.IsAvailable);
        Assert.Equal(0, service.Dimensions);
        Assert.Equal("", service.ModelId);
    }

    [Fact]
    public void Empty_models_directory_is_not_available()
    {
        using var temp = new TempDirectory();

        using var service = Create(temp.Path);

        Assert.False(service.IsAvailable);
    }

    [Fact]
    public async Task Embed_without_model_throws_invalid_operation()
    {
        using var temp = new TempDirectory();
        using var service = Create(temp.Path);

        var docs = await Assert.ThrowsAsync<InvalidOperationException>(() => service.EmbedDocumentsAsync(["a"], CancellationToken.None));
        var query = await Assert.ThrowsAsync<InvalidOperationException>(() => service.EmbedQueryAsync("a", CancellationToken.None));

        Assert.Equal("Embedding model is not available", docs.Message);
        Assert.Equal("Embedding model is not available", query.Message);
    }

    [Fact]
    public void Folder_with_complete_files_is_available_without_loading_the_model()
    {
        using var temp = new TempDirectory();
        WriteModel(temp.Path, "test-model");

        // model.onnx is garbage: IsAvailable must only look at files, so it must not try to load it.
        using var service = Create(temp.Path);

        Assert.True(service.IsAvailable);
        Assert.Equal("test/model", service.ModelId);
        Assert.Equal(4, service.Dimensions);
    }

    [Fact]
    public async Task Corrupt_model_file_fails_on_first_embed_and_marks_service_unavailable()
    {
        using var temp = new TempDirectory();
        WriteModel(temp.Path, "test-model");
        using var service = Create(temp.Path);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.EmbedQueryAsync("hi", CancellationToken.None));

        Assert.Equal("Embedding model is not available", ex.Message);
        Assert.False(service.IsAvailable);
    }

    [Fact]
    public void Missing_model_or_vocab_file_is_not_available()
    {
        using var noModel = new TempDirectory();
        WriteModel(noModel.Path, "m", withModel: false);
        using var noVocab = new TempDirectory();
        WriteModel(noVocab.Path, "m", withVocab: false);

        using var a = Create(noModel.Path);
        using var b = Create(noVocab.Path);

        Assert.False(a.IsAvailable);
        Assert.False(b.IsAvailable);
    }

    [Fact]
    public void Unsupported_tokenizer_is_not_available_and_logs_an_error()
    {
        using var temp = new TempDirectory();
        WriteModel(temp.Path, "m", ManifestJson.Replace("\"wordpiece\"", "\"unigram\""));
        var logger = new TestLogger<OnnxEmbeddingService>();

        using var service = Create(temp.Path, logger);

        Assert.False(service.IsAvailable);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("unigram"));
    }

    [Fact]
    public void Invalid_manifest_is_not_available_and_logs_an_error()
    {
        using var temp = new TempDirectory();
        WriteModel(temp.Path, "m", "{ \"id\": \"x\" }");
        var logger = new TestLogger<OnnxEmbeddingService>();

        using var service = Create(temp.Path, logger);

        Assert.False(service.IsAvailable);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public void First_folder_in_name_order_is_used_unless_default_file_says_otherwise()
    {
        using var temp = new TempDirectory();
        WriteModel(temp.Path, "a-model", ManifestJson.Replace("test/model", "test/a"));
        WriteModel(temp.Path, "b-model", ManifestJson.Replace("test/model", "test/b"));
        Directory.CreateDirectory(Path.Combine(temp.Path, "0-not-a-model"));

        using (var service = Create(temp.Path))
        {
            Assert.Equal("test/a", service.ModelId);
        }

        File.WriteAllText(Path.Combine(temp.Path, "default"), "b-model\n");
        using (var service = Create(temp.Path))
        {
            Assert.Equal("test/b", service.ModelId);
        }
    }

    [Fact]
    public async Task Logs_never_contain_document_text()
    {
        using var temp = new TempDirectory();
        WriteModel(temp.Path, "m");
        var logger = new TestLogger<OnnxEmbeddingService>();
        using var service = Create(temp.Path, logger);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.EmbedQueryAsync("secret-document-text", CancellationToken.None));

        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("secret-document-text"));
    }
}

/// <summary>Tests that need the real model (run <c>bash tools/download-models.sh</c>); skipped when it is missing.</summary>
public sealed class OnnxEmbeddingServiceModelTests : IDisposable
{
    private readonly OnnxEmbeddingService? _service;
    private readonly ITestOutputHelper _output;

    public OnnxEmbeddingServiceModelTests(ITestOutputHelper output)
    {
        _output = output;
        var models = FindModelsDirectory();
        if (models is not null)
        {
            _service = new OnnxEmbeddingService(models, NullLogger<OnnxEmbeddingService>.Instance);
        }
    }

    public void Dispose() => _service?.Dispose();

    private OnnxEmbeddingService Service
    {
        get
        {
            Skip.If(_service is not { IsAvailable: true }, "Embedding model not found; run tools/download-models.sh");
            return _service!;
        }
    }

    internal static string? FindModelsDirectory()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("CONTEXO_MODELS_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnvironment) && Directory.Exists(fromEnvironment))
        {
            return fromEnvironment;
        }

        var root = FindRepositoryRoot();
        var models = root is null ? null : Path.Combine(root, "models");
        return models is not null && Directory.Exists(models) ? models : null;
    }

    internal static string? FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Contexo.slnx")))
            {
                return directory.FullName;
            }
        }

        return null;
    }

    private static double Dot(float[] a, float[] b)
    {
        double sum = 0;
        for (var i = 0; i < a.Length; i++)
        {
            sum += (double)a[i] * b[i];
        }

        return sum;
    }

    [SkippableFact]
    public async Task Vector_has_model_dimensions_and_unit_length()
    {
        var service = Service;

        var vector = await service.EmbedQueryAsync("報價單", CancellationToken.None);
        var documents = await service.EmbedDocumentsAsync(["這是給客戶的報價明細", "Hello, World!", ""], CancellationToken.None);

        Assert.Equal("bge-small-zh-v1.5/int8", service.ModelId);
        Assert.Equal(512, service.Dimensions);
        Assert.Equal(512, vector.Length);
        Assert.Equal(1.0, Math.Sqrt(Dot(vector, vector)), 1e-4);
        Assert.Equal(3, documents.Count);
        Assert.All(documents, d =>
        {
            Assert.Equal(512, d.Length);
            Assert.Equal(1.0, Math.Sqrt(Dot(d, d)), 1e-4);
        });
    }

    [SkippableFact]
    public async Task Same_text_gives_identical_vectors_and_empty_input_gives_empty_result()
    {
        var service = Service;

        var first = await service.EmbedDocumentsAsync(["監視系統報價"], CancellationToken.None);
        var second = await service.EmbedDocumentsAsync(["監視系統報價"], CancellationToken.None);

        Assert.Equal(first[0], second[0]);
        Assert.Empty(await service.EmbedDocumentsAsync([], CancellationToken.None));
    }

    [SkippableFact]
    public async Task Batch_result_matches_single_results_and_keeps_input_order()
    {
        var service = Service;
        var texts = Enumerable.Range(0, 40).Select(i => string.Concat(Enumerable.Repeat("報價單與合約", (i * 7) % 11 + 1)) + i).ToArray();

        var batch = await service.EmbedDocumentsAsync(texts, CancellationToken.None);

        Assert.Equal(texts.Length, batch.Count);
        foreach (var i in new[] { 0, 5, 17, 39 })
        {
            var single = (await service.EmbedDocumentsAsync([texts[i]], CancellationToken.None))[0];
            // The int8 model quantises activations per tensor, so padding changes results slightly (cosine ~0.99).
            Assert.True(Dot(single, batch[i]) > 0.97, $"item {i} differs between batch and single run");
        }
    }

    [SkippableTheory]
    [InlineData("報價單", "這是給客戶的報價明細", "今天中午吃什麼")]
    [InlineData("合約到期日", "本合約有效期間至二〇二六年十二月三十一日止", "週末去哪裡爬山")]
    [InlineData("員工請假規定", "員工請假須於三日前提出申請並經主管核准", "最新款手機的相機評價")]
    [InlineData("退貨流程", "客戶如需退貨，請於七日內填寫退貨申請單", "颱風假是否照常上班上課")]
    [InlineData("伺服器當機怎麼辦", "系統發生當機時，請先重新啟動伺服器並通知資訊部門", "蛋糕的食譜與烘焙技巧")]
    public async Task Related_text_scores_higher_than_unrelated_text(string query, string related, string unrelated)
    {
        var service = Service;

        var q = await service.EmbedQueryAsync(query, CancellationToken.None);
        var docs = await service.EmbedDocumentsAsync([related, unrelated], CancellationToken.None);

        Assert.True(Dot(q, docs[0]) > Dot(q, docs[1]), $"'{related}' should be closer to '{query}' than '{unrelated}'");
    }

    [SkippableFact]
    public async Task Concurrent_calls_match_single_threaded_results()
    {
        var service = Service;
        string[] texts = ["報價單", "合約到期日", "Hello, World!", "員工請假規定", "退貨流程", "伺服器當機", "會議紀錄", "供應商名單"];
        var expected = new List<float[]>();
        foreach (var text in texts)
        {
            expected.Add((await service.EmbedDocumentsAsync([text], CancellationToken.None))[0]);
        }

        var tasks = texts.Select(async (text, i) =>
        {
            var result = await Task.Run(() => service.EmbedDocumentsAsync([text, text], CancellationToken.None));
            return (i, result);
        }).ToArray();
        var results = await Task.WhenAll(tasks);

        foreach (var (i, result) in results)
        {
            Assert.Equal(2, result.Count);
            Assert.True(Dot(expected[i], result[0]) > 0.9999);
            Assert.True(Dot(expected[i], result[1]) > 0.9999);
        }
    }

    [SkippableFact]
    public async Task Cancelled_token_stops_embedding()
    {
        var service = Service;
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.EmbedDocumentsAsync(["a"], cts.Token));
    }

    [SkippableFact]
    public async Task Matches_python_reference_for_token_ids_and_vector_prefix()
    {
        var service = Service;
        var root = FindRepositoryRoot();
        var path = root is null ? null : Path.Combine(root, "tests", "Contexo.Core.Tests", "Fixtures", "Embedding", "reference.json");
        Skip.If(path is null || !File.Exists(path), "reference.json not found; run tools/make-embedding-fixtures.py");

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path!));
        var manifest = ModelManifest.Load(Path.Combine(FindModelsDirectory()!, "bge-small-zh-v1.5"));
        var tokenizer = new WordPieceTokenizer(
            WordPieceTokenizer.LoadVocab(Path.Combine(FindModelsDirectory()!, "bge-small-zh-v1.5", manifest.Vocab)), manifest.Lowercase, manifest.MaxTokens);

        var checkedCases = 0;
        foreach (var testCase in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            var text = testCase.GetProperty("text").GetString()!;
            var isQuery = testCase.GetProperty("mode").GetString() == "query";
            var prefix = isQuery ? manifest.QueryPrefix : manifest.PassagePrefix;

            var expectedIds = testCase.GetProperty("ids").EnumerateArray().Select(e => e.GetInt32()).ToArray();
            Assert.True(expectedIds.SequenceEqual(tokenizer.Encode(prefix + text)), $"token ids differ for '{text}'");

            var expectedVector = testCase.GetProperty("vector8").EnumerateArray().Select(e => (float)e.GetDouble()).ToArray();
            var actual = isQuery
                ? await service.EmbedQueryAsync(text, CancellationToken.None)
                : (await service.EmbedDocumentsAsync([text], CancellationToken.None))[0];
            for (var d = 0; d < expectedVector.Length; d++)
            {
                // int8 quantisation noise plus a different onnxruntime version than the Python reference: ~0.012 observed, so allow 0.03.
                Assert.True(Math.Abs(expectedVector[d] - actual[d]) < 3e-2, $"dimension {d} differs for '{text}': {expectedVector[d]} vs {actual[d]}");
            }

            checkedCases++;
        }

        Assert.True(checkedCases >= 20);
    }

    [SkippableFact]
    public async Task Throughput_of_100_passages_of_about_300_characters()
    {
        var service = Service;
        var sentence = "客戶來信詢問報價單的有效期限與付款方式，業務部門需在三個工作天內回覆，並附上最新的產品規格與交期說明。";
        var texts = Enumerable.Range(0, 100).Select(i => string.Concat(Enumerable.Repeat(sentence, 6)) + i).ToArray();
        await service.EmbedDocumentsAsync([texts[0]], CancellationToken.None); // load the model outside the measurement

        var stopwatch = Stopwatch.StartNew();
        var vectors = await service.EmbedDocumentsAsync(texts, CancellationToken.None);
        stopwatch.Stop();

        Assert.Equal(100, vectors.Count);
        _output.WriteLine($"Embedded 100 passages of ~{texts[0].Length} chars in {stopwatch.ElapsedMilliseconds} ms");
    }
}
