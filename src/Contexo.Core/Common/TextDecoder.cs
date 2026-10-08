using System.Text;

namespace Contexo.Core.Common;

/// <summary>Decodes text files of unknown encoding. Shared by the text and CSV parsers.</summary>
public static class TextDecoder
{
    private const double MinimumDetectionConfidence = 0.5;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Order: byte-order mark, strict UTF-8, UTF.Unknown detection (confidence at least 0.5), then Big5 (code page 950).
    /// The result has "\n" line endings and no NUL characters. Reads from the current position to the end of <paramref name="stream"/>.
    /// </summary>
    public static string Decode(Stream stream, out Encoding detected)
    {
        ArgumentNullException.ThrowIfNull(stream);

        EnsureCodePages();

        byte[] bytes;
        using (var buffer = new MemoryStream())
        {
            stream.CopyTo(buffer);
            bytes = buffer.ToArray();
        }

        var (encoding, bomLength) = DetectByteOrderMark(bytes);
        if (encoding is null)
        {
            encoding = DetectWithoutBom(bytes);
        }

        detected = encoding;
        var text = encoding.GetString(bytes, bomLength, bytes.Length - bomLength);
        return Normalize(text);
    }

    /// <summary>Registers legacy code pages (Big5, GBK, ...). Safe to call repeatedly.</summary>
    internal static void EnsureCodePages() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static (Encoding? Encoding, int BomLength) DetectByteOrderMark(byte[] b)
    {
        if (b.Length >= 4 && b[0] == 0xFF && b[1] == 0xFE && b[2] == 0 && b[3] == 0)
        {
            return (new UTF32Encoding(bigEndian: false, byteOrderMark: true), 4);
        }

        if (b.Length >= 4 && b[0] == 0 && b[1] == 0 && b[2] == 0xFE && b[3] == 0xFF)
        {
            return (new UTF32Encoding(bigEndian: true, byteOrderMark: true), 4);
        }

        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
        {
            return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 3);
        }

        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE)
        {
            return (new UnicodeEncoding(bigEndian: false, byteOrderMark: true), 2);
        }

        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF)
        {
            return (new UnicodeEncoding(bigEndian: true, byteOrderMark: true), 2);
        }

        return (null, 0);
    }

    private static Encoding DetectWithoutBom(byte[] bytes)
    {
        try
        {
            StrictUtf8.GetCharCount(bytes);
            return StrictUtf8;
        }
        catch (DecoderFallbackException)
        {
            // Not valid UTF-8; try detection below.
        }

        try
        {
            var detected = UtfUnknown.CharsetDetector.DetectFromBytes(bytes).Detected;
            if (detected is { Encoding: { } encoding } && detected.Confidence >= MinimumDetectionConfidence)
            {
                return encoding;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            // Detector named an encoding this platform cannot provide; fall back to Big5.
        }

        return Encoding.GetEncoding(950);
    }

    private static string Normalize(string text)
    {
        if (text.Contains('\0'))
        {
            text = text.Replace("\0", string.Empty, StringComparison.Ordinal);
        }

        if (text.Contains('\r'))
        {
            text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        }

        return text;
    }
}
