using System.IO.Compression;
using System.Text;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Contexo.Core.Tests.Parsing.Pdf;

/// <summary>Builds small PDFs in memory for the PDF parser tests. Nothing here is a real document.</summary>
internal static class PdfTestFiles
{
    /// <summary>A text line to draw: <paramref name="Y"/> is the PDF baseline (A4 page is 595 x 842).</summary>
    public sealed record TextLine(string Text, double Y, double X = 72, double FontSize = 12);

    /// <summary>One page per entry, each with the given lines (Helvetica).</summary>
    public static byte[] TextPdf(params IReadOnlyList<TextLine>[] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var lines in pages)
        {
            var page = builder.AddPage(595, 842);
            foreach (var line in lines)
            {
                page.AddText(line.Text, line.FontSize, new PdfPoint(line.X, line.Y), font);
            }
        }

        return builder.Build();
    }

    /// <summary>A PDF whose pages each consist of a single picture and have no text layer.</summary>
    public static byte[] ImageOnlyPdf(int pageCount)
    {
        var png = SolidPng(8, 8);
        var builder = new PdfDocumentBuilder();
        for (var i = 0; i < pageCount; i++)
        {
            var page = builder.AddPage(595, 842);
            page.AddPng(png, new PdfRectangle(50, 50, 500, 700));
        }

        return builder.Build();
    }

    /// <summary>
    /// A hand-written PDF with an /Encrypt dictionary whose user password check cannot succeed with the empty password.
    /// PdfPig cannot write encrypted files, so this is the smallest file that makes it report "encrypted".
    /// </summary>
    public static byte[] EncryptedPdf()
    {
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            $"<< /Filter /Standard /V 1 /R 2 /Length 40 /P -4 /O <{new string('A', 64)}> /U <{new string('B', 64)}> >>",
        };

        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        var xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append('\n');
        sb.Append("0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            sb.Append(offset.ToString("D10")).Append(" 00000 n \n");
        }

        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1)
            .Append(" /Root 1 0 R /Encrypt 4 0 R /ID [<00112233445566778899AABBCCDDEEFF> <00112233445566778899AABBCCDDEEFF>] >>\n");
        sb.Append("startxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>Minimal 8-bit RGB PNG of one colour, written by hand so no imaging library is needed.</summary>
    public static byte[] SolidPng(int width, int height)
    {
        using var raw = new MemoryStream();
        for (var y = 0; y < height; y++)
        {
            raw.WriteByte(0); // filter: none
            for (var x = 0; x < width; x++)
            {
                raw.Write([0x30, 0x60, 0x90]);
            }
        }

        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(raw.ToArray());
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // bit depth
        header[9] = 2; // colour type: RGB
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);

        var body = new byte[4 + data.Length];
        Encoding.ASCII.GetBytes(type, body);
        data.CopyTo(body, 4);
        stream.Write(body);

        var crc = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(body));
        stream.Write(crc);
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }
}
