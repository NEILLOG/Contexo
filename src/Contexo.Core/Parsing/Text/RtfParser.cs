using System.Text;
using Contexo.Core.Abstractions;

namespace Contexo.Core.Parsing.Text;

/// <summary>Parses .rtf files by converting them to HTML with RtfPipe and running the shared HTML extraction.</summary>
internal sealed class RtfParser : IDocumentParser
{
    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".rtf"];

    public Task<ParsedDocument> ParseAsync(ParseContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        // RTF is a 7-bit format: non-ASCII characters are written as escapes, so Latin-1 reads every byte unchanged.
        var rtf = Encoding.Latin1.GetString(ParserSupport.ReadAllBytes(context.Content)).TrimStart('﻿', ' ', '\r', '\n', '\t');
        if (rtf.Length == 0)
        {
            return Task.FromResult(ParsedDocument.Empty);
        }

        if (!rtf.StartsWith("{\\rtf", StringComparison.Ordinal))
        {
            throw new DocumentParseException(DocumentErrorCode.Corrupted, "這個檔案不是有效的 RTF 文件。");
        }

        string html;
        try
        {
            html = RtfPipe.Rtf.ToHtml(rtf);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new DocumentParseException(DocumentErrorCode.Corrupted, "RTF 文件無法讀取。", ex);
        }

        return Task.FromResult(HtmlContentExtractor.Extract(html, context, cancellationToken));
    }
}
