using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Contexo.CorpusGen.Office;

namespace Contexo.CorpusGen;

/// <summary>One query of the retrieval evaluation. Serialised to queries.json.</summary>
/// <param name="ExpectedFiles">Any one of these files in the top results counts as a hit (usually one).</param>
/// <param name="ExpectedLocation">Optional: text that should appear in the location of the best matching hit (heading, slide title, sheet, page).</param>
/// <param name="NeedsTable">The best matching hit must carry a TableId.</param>
/// <param name="ObserveOnly">Reported, but not part of the Recall gate (known gaps and exploratory questions).</param>
public sealed record QuerySpec(
    string Id,
    string Query,
    string[] ExpectedFiles,
    string Kind,
    string? ExpectedLocation = null,
    bool NeedsTable = false,
    bool ObserveOnly = false);

/// <summary>The correct answers for the SQL checks, computed from the generated data (not from Contexo).</summary>
public sealed class ExpectedValues
{
    public string SalesFile { get; set; } = "";
    public int SalesRowCount { get; set; }
    public long SalesTotalAmount { get; set; }
    public string SalesTopCustomer { get; set; } = "";
    public long SalesTopCustomerAmount { get; set; }
    public Dictionary<string, long> SalesAmountByCustomer { get; set; } = [];
    public string CustomerListFile { get; set; } = "";
    public int CustomerListRowCount { get; set; }
    public int CustomerListNorthCount { get; set; }
    public string OrderCsvFile { get; set; } = "";
    public int OrderCsvRowCount { get; set; }
    public int OrderCsvShippedCount { get; set; }
}

/// <summary>Collects the generated files so they can be written, listed and hashed in a fixed order.</summary>
internal sealed class CorpusBuilder
{
    private readonly SortedDictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly List<QuerySpec> _queries = [];

    public CorpusBuilder(int seed) => Random = new Random(seed);

    public Random Random { get; }

    public ExpectedValues Expected { get; } = new();

    public IReadOnlyDictionary<string, byte[]> Files => _files;

    public IReadOnlyList<QuerySpec> Queries => _queries;

    public void AddFile(string name, byte[] content)
    {
        if (!_files.TryAdd(name, content))
        {
            throw new InvalidOperationException("Duplicate corpus file: " + name);
        }
    }

    public void AddOffice(string name, byte[] package) => AddFile(name, ZipNormalizer.Normalize(package));

    public void AddQuery(string id, string query, string expectedFile, string kind, string? location = null, bool needsTable = false, bool observeOnly = false) =>
        AddQuery(id, query, [expectedFile], kind, location, needsTable, observeOnly);

    public void AddQuery(string id, string query, string[] expectedFiles, string kind, string? location = null, bool needsTable = false, bool observeOnly = false)
    {
        foreach (var file in expectedFiles)
        {
            if (!_files.ContainsKey(file))
            {
                throw new InvalidOperationException($"Query {id} expects a file that was not generated: {file}");
            }
        }

        _queries.Add(new QuerySpec(id, query, expectedFiles, kind, location, needsTable, observeOnly));
    }

    /// <summary>Writes the corpus to <c>{outputDirectory}/corpus</c> plus queries.json, expected.json and MANIFEST.txt next to it.</summary>
    public void WriteTo(string outputDirectory)
    {
        var corpusDirectory = Path.Combine(outputDirectory, "corpus");
        if (Directory.Exists(corpusDirectory))
        {
            Directory.Delete(corpusDirectory, recursive: true);
        }

        Directory.CreateDirectory(corpusDirectory);
        var manifest = new StringBuilder();
        foreach (var (name, content) in _files)
        {
            var path = Path.Combine(corpusDirectory, name);
            File.WriteAllBytes(path, content);
            File.SetLastWriteTimeUtc(path, new DateTime(2025, 6, 1, 8, 0, 0, DateTimeKind.Utc));
            manifest.Append(Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant()).Append("  ").Append(name).Append('\n');
        }

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        var queriesJson = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(_queries, options));
        var expectedJson = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Expected, options));
        File.WriteAllBytes(Path.Combine(outputDirectory, "queries.json"), queriesJson);
        File.WriteAllBytes(Path.Combine(outputDirectory, "expected.json"), expectedJson);
        manifest.Append(Convert.ToHexString(SHA256.HashData(queriesJson)).ToLowerInvariant()).Append("  queries.json\n");
        manifest.Append(Convert.ToHexString(SHA256.HashData(expectedJson)).ToLowerInvariant()).Append("  expected.json\n");
        File.WriteAllText(Path.Combine(outputDirectory, "MANIFEST.txt"), manifest.ToString(), new UTF8Encoding(false));
    }
}
