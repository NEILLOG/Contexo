using Contexo.CorpusGen.Content;

namespace Contexo.CorpusGen;

/// <summary>Builds the fictional-company corpus (40 files) and its query list. Same seed, same bytes.</summary>
public static class CorpusGenerator
{
    public const int Seed = 20251010;
    public const int FileCount = 40;

    /// <summary>Writes <c>{outputDirectory}/corpus/*</c>, <c>queries.json</c>, <c>expected.json</c> and <c>MANIFEST.txt</c>. An existing corpus folder is replaced.</summary>
    public static void Generate(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var corpus = new CorpusBuilder(Seed);
        WordDocuments.AddAll(corpus);
        PresentationDocuments.AddAll(corpus);
        SpreadsheetDocuments.AddAll(corpus);
        PdfDocuments.AddAll(corpus);
        TextDocuments.AddAll(corpus);
        if (corpus.Files.Count != FileCount)
        {
            throw new InvalidOperationException($"Expected {FileCount} files but generated {corpus.Files.Count}.");
        }

        QuerySet.AddAll(corpus);
        corpus.WriteTo(outputDirectory);
    }
}
