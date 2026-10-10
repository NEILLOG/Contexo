using System.Text.Json;
using System.Text.Json.Serialization;
using Contexo.Core;
using Contexo.Core.Abstractions;
using Contexo.CorpusGen;
using Microsoft.Extensions.DependencyInjection;

const string Usage = """
    Contexo.CorpusGen

    用法：
      generate <資料夾>
          產生測試語料到 <資料夾>/corpus，並寫出 queries.json、expected.json、MANIFEST.txt。固定亂數種子，重跑結果相同。
      parse <檔案...>
          用 Contexo 的解析器與切塊器讀取檔案，把每個片段的位置與文字印到標準輸出（檢查真實軟體另存的檔案用）。
      eval [--models <資料夾>] [--corpus <已產生的資料夾>] [--keep]
          對 --models 資料夾中的每個模型各建一次索引，輸出 Recall@1/3/5、MRR、耗時的 Markdown 表格到標準輸出。
          沒有給 --models 時只跑「沒有模型、只用關鍵字」的一列。沒有給 --corpus 時先產生到暫存資料夾。
    """;

if (args.Length == 0)
{
    Console.Error.WriteLine(Usage);
    return 2;
}

try
{
    switch (args[0])
    {
        case "generate" when args.Length == 2:
            CorpusGenerator.Generate(Path.GetFullPath(args[1]));
            Console.Error.WriteLine($"已產生 {CorpusGenerator.FileCount} 個檔案到 {Path.Combine(Path.GetFullPath(args[1]), "corpus")}");
            return 0;

        case "eval":
            return await EvalAsync(args[1..]);

        case "parse" when args.Length >= 2:
            return await ParseAsync(args[1..]);

        default:
            Console.Error.WriteLine(Usage);
            return 2;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine("失敗：" + ex);
    return 1;
}

static async Task<int> EvalAsync(string[] args)
{
    string? models = null;
    string? corpus = null;
    var keep = false;
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--models" when i + 1 < args.Length:
                models = Path.GetFullPath(args[++i]);
                break;
            case "--corpus" when i + 1 < args.Length:
                corpus = Path.GetFullPath(args[++i]);
                break;
            case "--keep":
                keep = true;
                break;
            default:
                Console.Error.WriteLine("無法辨識的參數：" + args[i]);
                return 2;
        }
    }

    var temporary = corpus is null ? Path.Combine(Path.GetTempPath(), "contexo-corpus-" + Guid.NewGuid().ToString("N")) : null;
    corpus ??= temporary!;
    try
    {
        if (temporary is not null)
        {
            CorpusGenerator.Generate(corpus);
        }

        var reports = new List<ModelReport>();
        var modelDirectories = models is null || !Directory.Exists(models)
            ? []
            : Directory.GetDirectories(models).Where(d => File.Exists(Path.Combine(d, "contexo-model.json"))).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (models is not null && modelDirectories.Count == 0)
        {
            Console.Error.WriteLine("在 --models 資料夾中找不到任何含 contexo-model.json 的模型，只輸出關鍵字模式。");
        }

        foreach (var directory in modelDirectories)
        {
            Console.Error.WriteLine("評估模型：" + Path.GetFileName(directory));
            reports.Add(await Evaluation.RunAsync(corpus, Path.GetFileName(directory), directory, CancellationToken.None, keep));
        }

        Console.Error.WriteLine("評估：只用關鍵字");
        reports.Add(await Evaluation.RunAsync(corpus, "（無模型，只用關鍵字）", null, CancellationToken.None, keep));

        Console.Out.Write(Evaluation.FormatTable(reports));
        return 0;
    }
    finally
    {
        if (temporary is not null && Directory.Exists(temporary))
        {
            Directory.Delete(temporary, recursive: true);
        }
    }
}

static async Task<int> ParseAsync(string[] files)
{
    var services = new ServiceCollection();
    services.AddContexoCore();
    await using var provider = services.BuildServiceProvider();
    var registry = provider.GetRequiredService<IParserRegistry>();
    var chunker = provider.GetRequiredService<IChunker>();
    var json = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    foreach (var path in files)
    {
        var parser = registry.Resolve(Path.GetExtension(path));
        if (parser is null)
        {
            Console.WriteLine($"===== {Path.GetFileName(path)}: 不支援的檔案類型");
            continue;
        }

        await using var stream = File.OpenRead(path);
        var parsed = await parser.ParseAsync(new ParseContext(stream, Path.GetFileName(path), new ParserOptions()), CancellationToken.None);
        Console.WriteLine($"===== {Path.GetFileName(path)}: {parsed.Sections.Count} 個區段，{parsed.Tables.Count} 張大表，{parsed.EmbeddedFiles.Count} 個內嵌檔，警告：{string.Join("；", parsed.Warnings)}");
        foreach (var chunk in chunker.Split(Path.GetFileNameWithoutExtension(path), parsed.Sections, new ChunkingOptions()))
        {
            Console.WriteLine($"--- 片段 {chunk.Ordinal} [{chunk.Kind}] {JsonSerializer.Serialize(chunk.Location, json)}");
            Console.WriteLine(chunk.Text);
        }
    }

    return 0;
}
