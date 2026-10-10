using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Contexo.Core;
using Contexo.Core.Abstractions;
using Contexo.Core.Common;
using Microsoft.Extensions.DependencyInjection;

namespace Contexo.CorpusGen;

/// <summary>Outcome of one query against one model.</summary>
internal sealed record QueryOutcome(QuerySpec Query, int? Rank, bool LocationMatched, bool? TableMatched, string? TopFile, bool Degraded);

internal sealed record ModelReport(
    string Name,
    bool ModelAvailable,
    int Dimensions,
    int FileCount,
    int FailedFiles,
    int ChunkCount,
    TimeSpan IndexTime,
    double EmbedMillisecondsPerChunk,
    double Recall1,
    double Recall3,
    double Recall5,
    double Mrr,
    IReadOnlyList<QueryOutcome> Outcomes,
    SimilaritySpread? Similarity);

/// <summary>Highest cosine similarity between a query and any stored chunk vector: min / median / max over a set of queries.</summary>
internal sealed record SimilaritySpread(int AnswerableCount, double AnswerableMin, double AnswerableMedian, double AnswerableMax, int OffTopicCount, double OffTopicMin, double OffTopicMedian, double OffTopicMax, int AnswerableNotAboveOffTopicMax);

/// <summary>Indexes the generated corpus with the real Contexo services and measures retrieval quality.</summary>
internal static class Evaluation
{
    public static async Task<ModelReport> RunAsync(string corpusRoot, string name, string? modelDirectory, CancellationToken cancellationToken, bool keepWorkFolder = false)
    {
        var work = Path.Combine(Path.GetTempPath(), "contexo-eval-" + Guid.NewGuid().ToString("N"));
        var dataDirectory = Path.Combine(work, "data");
        var modelsRoot = Path.Combine(work, "models");
        Directory.CreateDirectory(dataDirectory);
        Directory.CreateDirectory(modelsRoot);
        try
        {
            if (modelDirectory is not null)
            {
                var target = Path.Combine(modelsRoot, Path.GetFileName(modelDirectory));
                Directory.CreateDirectory(target);
                foreach (var file in Directory.GetFiles(modelDirectory))
                {
                    File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
                }

                File.WriteAllText(Path.Combine(modelsRoot, "default"), Path.GetFileName(modelDirectory));
            }

            var services = new ServiceCollection();
            services.AddSingleton<IAppPaths>(new AppPaths(new AppPathsOverrides { DataDirectory = dataDirectory, ModelsDirectory = modelsRoot }));
            services.AddContexoCore();
            await using var provider = services.BuildServiceProvider();

            var store = provider.GetRequiredService<IKnowledgeStore>();
            await store.InitializeAsync(cancellationToken);
            var folder = await store.AddFolderAsync(Path.Combine(corpusRoot, "corpus"), cancellationToken);
            var indexing = provider.GetRequiredService<IIndexingService>();
            var embedding = provider.GetRequiredService<IEmbeddingService>();

            var clock = Stopwatch.StartNew();
            await indexing.StartAsync(cancellationToken);
            await WaitUntilIdleAsync(indexing, store, folder.Id, Directory.GetFiles(folder.Path).Length, TimeSpan.FromMinutes(30), cancellationToken);
            clock.Stop();
            await indexing.StopAsync(cancellationToken);

            var documents = await store.GetDocumentsAsync(folder.Id, cancellationToken);
            var statistics = await store.GetStatisticsAsync(cancellationToken);
            var queries = JsonSerializer.Deserialize<List<QuerySpec>>(await File.ReadAllTextAsync(Path.Combine(corpusRoot, "queries.json"), cancellationToken), JsonOptions)!;
            var search = provider.GetRequiredService<ISearchService>();

            var outcomes = new List<QueryOutcome>();
            var sampleTexts = new List<string>();
            foreach (var query in queries)
            {
                var response = await search.SearchAsync(new SearchRequest(query.Query, 10), cancellationToken);
                outcomes.Add(Judge(query, response));
                sampleTexts.AddRange(response.Hits.Select(h => h.Text));
            }

            var embedMs = 0.0;
            if (embedding.IsAvailable)
            {
                var sample = sampleTexts.Distinct().Take(200).ToList();
                await embedding.EmbedDocumentsAsync(sample.Take(4).ToList(), cancellationToken); // warm-up
                var timer = Stopwatch.StartNew();
                await embedding.EmbedDocumentsAsync(sample, cancellationToken);
                timer.Stop();
                embedMs = timer.Elapsed.TotalMilliseconds / Math.Max(1, sample.Count);
            }

            SimilaritySpread? similarity = null;
            if (embedding.IsAvailable)
            {
                var vectors = new List<float[]>();
                await foreach (var stored in store.ReadVectorsAsync(embedding.ModelId, cancellationToken))
                {
                    vectors.Add(stored.Vector);
                }

                async Task<double> BestAsync(string text)
                {
                    var query = await embedding.EmbedQueryAsync(text, cancellationToken);
                    var best = double.MinValue;
                    foreach (var vector in vectors)
                    {
                        double dot = 0;
                        for (var i = 0; i < vector.Length; i++)
                        {
                            dot += (double)vector[i] * query[i];
                        }

                        best = Math.Max(best, dot);
                    }

                    return best;
                }

                var answerable = new List<double>();
                foreach (var query in queries.Where(q => !q.ObserveOnly))
                {
                    answerable.Add(await BestAsync(query.Query));
                }

                var offTopic = new List<double>();
                foreach (var text in OffTopicQueries)
                {
                    offTopic.Add(await BestAsync(text));
                }

                answerable.Sort();
                offTopic.Sort();
                similarity = new SimilaritySpread(
                    answerable.Count, answerable[0], answerable[answerable.Count / 2], answerable[^1],
                    offTopic.Count, offTopic[0], offTopic[offTopic.Count / 2], offTopic[^1], answerable.Count(a => a <= offTopic[^1]));
            }

            var gate = outcomes.Where(o => !o.Query.ObserveOnly).ToList();
            double Recall(int k) => gate.Count == 0 ? 0 : gate.Count(o => o.Rank is { } r && r <= k) / (double)gate.Count;
            var mrr = gate.Count == 0 ? 0 : gate.Average(o => o.Rank is { } r ? 1.0 / r : 0);
            return new ModelReport(
                name,
                embedding.IsAvailable,
                embedding.Dimensions,
                documents.Count,
                documents.Count(d => d.Status == DocumentStatus.Failed),
                statistics.ChunkCount,
                clock.Elapsed,
                embedMs,
                Recall(1),
                Recall(3),
                Recall(5),
                mrr,
                outcomes,
                similarity);
        }
        finally
        {
            if (keepWorkFolder)
            {
                Console.Error.WriteLine("保留工作資料夾：" + work);
            }
            else
            {
                try
                {
                    Directory.Delete(work, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    /// <summary>Questions the corpus cannot answer. Used to see how high an unrelated query still scores (there is no similarity floor, task T11).</summary>
    internal static readonly string[] OffTopicQueries =
    [
        "今天天氣如何", "明天會下雨嗎", "怎麼煮咖哩飯", "台積電的股價", "世界盃足球賽冠軍",
        "how to bake bread", "weather forecast for tomorrow", "量子力學的基本原理", "最近有什麼好看的電影", "貓咪不吃飯怎麼辦",
    ];

    internal static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static QueryOutcome Judge(QuerySpec query, SearchResponse response)
    {
        int? rank = null;
        var locationMatched = false;
        bool? tableMatched = query.NeedsTable ? false : null;
        for (var i = 0; i < response.Hits.Count; i++)
        {
            var hit = response.Hits[i];
            if (!query.ExpectedFiles.Contains(hit.FileName, StringComparer.Ordinal))
            {
                continue;
            }

            rank ??= i + 1;
            if (query.ExpectedLocation is { } location && DescribeLocation(hit.Location).Contains(location, StringComparison.OrdinalIgnoreCase))
            {
                locationMatched = true;
            }

            if (query.NeedsTable && hit.TableId is not null)
            {
                tableMatched = true;
            }
        }

        return new QueryOutcome(query, rank, locationMatched, tableMatched, response.Hits.FirstOrDefault()?.FileName, response.Degraded);
    }

    public static string DescribeLocation(SourceLocation location)
    {
        var parts = new List<string>();
        if (location.HeadingPath is { Count: > 0 } path)
        {
            parts.AddRange(path);
        }

        if (location.Title is not null)
        {
            parts.Add(location.Title);
        }

        if (location.Sheet is not null)
        {
            parts.Add(location.Sheet);
        }

        if (location.Slide is { } slide)
        {
            parts.Add($"slide {slide}");
        }

        if (location.Page is { } page)
        {
            parts.Add($"page {page}");
        }

        return string.Join(" / ", parts);
    }

    public static async Task WaitUntilIdleAsync(IIndexingService indexing, IKnowledgeStore store, long folderId, int expectedFiles, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        var stable = 0;
        while (deadline.Elapsed < timeout)
        {
            await Task.Delay(250, cancellationToken);
            var documents = (await store.GetDocumentsAsync(folderId, cancellationToken)).Count;
            var snapshot = indexing.Current;
            var idle = snapshot.State == IndexingState.Idle && documents >= expectedFiles && snapshot.Folders.All(f => f.PendingFiles == 0);
            stable = idle ? stable + 1 : 0;
            if (stable >= 3)
            {
                return;
            }
        }

        throw new TimeoutException($"Indexing did not become idle within {timeout}.");
    }

    public static string FormatTable(IReadOnlyList<ModelReport> reports)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("| 模型 | 向量 | 檔案 | 失敗 | 片段 | Recall@1 | Recall@3 | Recall@5 | MRR | 索引耗時 | 每片段嵌入 |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var r in reports)
        {
            sb.AppendLine(string.Join(" | ",
                "| " + r.Name,
                r.ModelAvailable ? r.Dimensions.ToString(inv) + " 維" : "無（只用關鍵字）",
                r.FileCount.ToString(inv),
                r.FailedFiles.ToString(inv),
                r.ChunkCount.ToString(inv),
                r.Recall1.ToString("0.00", inv),
                r.Recall3.ToString("0.00", inv),
                r.Recall5.ToString("0.00", inv),
                r.Mrr.ToString("0.000", inv),
                r.IndexTime.TotalSeconds.ToString("0.0", inv) + " 秒",
                (r.ModelAvailable ? r.EmbedMillisecondsPerChunk.ToString("0.0", inv) + " ms" : "-") + " |"));
        }

        if (reports.Any(r => r.Similarity is not null))
        {
            sb.AppendLine();
            sb.AppendLine("### 語意相似度（查詢與最相近片段的內積）：有答案的題目與無關題目是否分得開");
            sb.AppendLine();
            sb.AppendLine("| 模型 | 有答案的題目（最低 / 中位 / 最高） | 無關的題目（最低 / 中位 / 最高） | 有答案但不高於無關題最高值的題數 |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var r in reports.Where(r => r.Similarity is not null))
            {
                var x = r.Similarity!;
                sb.AppendLine($"| {r.Name} | {x.AnswerableMin:0.000} / {x.AnswerableMedian:0.000} / {x.AnswerableMax:0.000}（{x.AnswerableCount} 題）| {x.OffTopicMin:0.000} / {x.OffTopicMedian:0.000} / {x.OffTopicMax:0.000}（{x.OffTopicCount} 題）| {x.AnswerableNotAboveOffTopicMax} |");
            }
        }

        foreach (var r in reports)
        {
            sb.AppendLine();
            sb.AppendLine($"### {r.Name}：逐題結果");
            sb.AppendLine();
            sb.AppendLine("| 題號 | 類型 | 查詢 | 預期檔案 | 名次 | 第一名檔案 | 備註 |");
            sb.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var o in r.Outcomes)
            {
                var notes = new List<string>();
                if (o.Query.ObserveOnly)
                {
                    notes.Add("只觀察");
                }

                if (o.Query.ExpectedLocation is not null)
                {
                    notes.Add(o.LocationMatched ? "位置符合" : "位置不符");
                }

                if (o.TableMatched is { } table)
                {
                    notes.Add(table ? "有 TableId" : "沒有 TableId");
                }

                sb.AppendLine($"| {o.Query.Id} | {o.Query.Kind} | {o.Query.Query} | {string.Join(" 或 ", o.Query.ExpectedFiles)} | {(o.Rank?.ToString(inv) ?? "未命中")} | {o.TopFile} | {string.Join("、", notes)} |");
            }
        }

        return sb.ToString();
    }
}
