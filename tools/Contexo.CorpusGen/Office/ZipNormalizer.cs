using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Contexo.CorpusGen.Office;

/// <summary>
/// The Open XML SDK stamps every zip entry with the current time and gives relationships random ids,
/// so two runs would differ byte for byte. Rewriting the archive with a fixed timestamp and numbered relationship ids
/// makes the generator output reproducible.
/// </summary>
internal static partial class ZipNormalizer
{
    private static readonly DateTimeOffset FixedTime = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [GeneratedRegex("R[0-9a-f]{16}")]
    private static partial Regex RandomRelationshipId();

    public static byte[] Normalize(byte[] package)
    {
        using var source = new MemoryStream(package);
        using var input = new ZipArchive(source, ZipArchiveMode.Read);
        var entries = input.Entries.OrderBy(e => e.FullName != "[Content_Types].xml").ThenBy(e => e.FullName, StringComparer.Ordinal).ToList();

        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        using var output = new MemoryStream();
        using (var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in entries)
            {
                byte[] bytes;
                using (var read = entry.Open())
                using (var buffer = new MemoryStream())
                {
                    read.CopyTo(buffer);
                    bytes = buffer.ToArray();
                }

                var isXml = entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase);
                if (isXml)
                {
                    var text = new UTF8Encoding(false).GetString(bytes);
                    text = RandomRelationshipId().Replace(text, match =>
                    {
                        if (!ids.TryGetValue(match.Value, out var replacement))
                        {
                            replacement = "rIdC" + (ids.Count + 1);
                            ids[match.Value] = replacement;
                        }

                        return replacement;
                    });
                    bytes = new UTF8Encoding(false).GetBytes(text);
                }

                var copy = target.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                copy.LastWriteTime = FixedTime;
                using var write = copy.Open();
                write.Write(bytes);
            }
        }

        return output.ToArray();
    }
}
