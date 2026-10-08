using System.Text;
using System.Text.RegularExpressions;
using Contexo.Core.Abstractions;
using Contexo.Core.Common;

namespace Contexo.Core.Parsing.Text;

/// <summary>Parses .html / .htm files. Encoding: byte-order mark, then <c>&lt;meta charset&gt;</c>, then <see cref="TextDecoder"/>.</summary>
internal sealed partial class HtmlParser : IDocumentParser
{
    private const int SniffLength = 4096;

    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".html", ".htm"];

    public Task<ParsedDocument> ParseAsync(ParseContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var html = Decode(ParserSupport.ReadAllBytes(context.Content));
        if (string.IsNullOrWhiteSpace(html))
        {
            return Task.FromResult(ParsedDocument.Empty);
        }

        return Task.FromResult(HtmlContentExtractor.Extract(html, context, cancellationToken));
    }

    private static string Decode(byte[] bytes)
    {
        if (!HasByteOrderMark(bytes) && TryGetDeclaredEncoding(bytes) is { } encoding)
        {
            var text = encoding.GetString(bytes);
            if (text.Length > 0 && text[0] == '﻿')
            {
                text = text[1..];
            }

            return text;
        }

        using var stream = new MemoryStream(bytes, writable: false);
        return TextDecoder.Decode(stream, out _);
    }

    private static bool HasByteOrderMark(byte[] b) =>
        (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
        || (b.Length >= 2 && ((b[0] == 0xFF && b[1] == 0xFE) || (b[0] == 0xFE && b[1] == 0xFF)));

    private static Encoding? TryGetDeclaredEncoding(byte[] bytes)
    {
        var head = Encoding.Latin1.GetString(bytes, 0, Math.Min(bytes.Length, SniffLength));
        var match = MetaCharsetRegex().Match(head);
        if (!match.Success)
        {
            return null;
        }

        try
        {
            TextDecoder.EnsureCodePages();
            return Encoding.GetEncoding(match.Groups[1].Value);
        }
        catch (ArgumentException)
        {
            // Unknown or unsupported charset name; fall back to detection.
            return null;
        }
    }

    [GeneratedRegex(@"<meta\b[^>]*?charset\s*=\s*[""']?\s*([A-Za-z0-9_\-:.]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MetaCharsetRegex();
}
