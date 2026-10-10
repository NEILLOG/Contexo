using System.Text;
using System.Text.RegularExpressions;

namespace Contexo.CorpusGen.Office;

/// <summary>PdfPig writes a random /ID into the trailer; replacing it with a constant of the same length keeps every xref offset valid.</summary>
internal static partial class PdfNormalizer
{
    private const string FixedId = "/ID [ <00112233445566778899AABBCCDDEEFF><FFEEDDCCBBAA99887766554433221100>]";

    [GeneratedRegex(@"/ID \[ ?<[0-9A-Fa-f]{32}> ?<[0-9A-Fa-f]{32}> ?\]")]
    private static partial Regex TrailerId();

    public static byte[] Normalize(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var replaced = TrailerId().Replace(text, match => FixedId.Length <= match.Length ? FixedId.PadRight(match.Length) : throw new InvalidOperationException("Unexpected PDF trailer id format"));
        return Encoding.Latin1.GetBytes(replaced);
    }
}
