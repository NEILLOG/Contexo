using System.Text;
using Contexo.Core.Common;

namespace Contexo.Core.Tests.Common;

public sealed class TextDecoderTests
{
    private const string Sample = "報價單：新台幣一萬二千元整，含稅。\n第二行：驗收標準與付款條件，請於三十日內完成匯款。\n第三行：聯絡人王小明，電話 02-1234-5678。";

    private static string Decode(byte[] bytes, out Encoding detected)
    {
        using var stream = new MemoryStream(bytes);
        return TextDecoder.Decode(stream, out detected);
    }

    private static byte[] WithPreamble(Encoding encoding, string text) =>
        [.. encoding.GetPreamble(), .. encoding.GetBytes(text)];

    [Fact]
    public void Utf8_without_bom()
    {
        var text = Decode(new UTF8Encoding(false).GetBytes(Sample), out var encoding);

        Assert.Equal(Sample, text);
        Assert.Equal(65001, encoding.CodePage);
    }

    [Fact]
    public void Utf8_with_bom_drops_the_bom()
    {
        var text = Decode(WithPreamble(new UTF8Encoding(true), Sample), out var encoding);

        Assert.Equal(Sample, text);
        Assert.Equal(65001, encoding.CodePage);
        Assert.DoesNotContain('﻿', text);
    }

    [Fact]
    public void Utf16_little_endian_with_bom()
    {
        var text = Decode(WithPreamble(new UnicodeEncoding(false, true), Sample), out var encoding);

        Assert.Equal(Sample, text);
        Assert.Equal(1200, encoding.CodePage);
    }

    [Fact]
    public void Utf16_big_endian_with_bom()
    {
        var text = Decode(WithPreamble(new UnicodeEncoding(true, true), Sample), out var encoding);

        Assert.Equal(Sample, text);
        Assert.Equal(1201, encoding.CodePage);
    }

    [Fact]
    public void Utf32_little_endian_with_bom_is_not_mistaken_for_utf16()
    {
        var text = Decode(WithPreamble(new UTF32Encoding(false, true), Sample), out var encoding);

        Assert.Equal(Sample, text);
        Assert.Equal(12000, encoding.CodePage);
    }

    [Fact]
    public void Big5_is_detected_and_traditional_chinese_is_restored()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var big5 = Encoding.GetEncoding(950);

        var text = Decode(big5.GetBytes(Sample), out var encoding);

        Assert.Equal(Sample, text);
        Assert.Equal(950, encoding.CodePage);
    }

    [Fact]
    public void Line_endings_are_normalised_to_lf()
    {
        var text = Decode("a\r\nb\rc\nd"u8.ToArray(), out _);

        Assert.Equal("a\nb\nc\nd", text);
    }

    [Fact]
    public void Nul_characters_are_removed()
    {
        var text = Decode("ab\0cd\0"u8.ToArray(), out _);

        Assert.Equal("abcd", text);
    }

    [Fact]
    public void Empty_input_gives_an_empty_string()
    {
        var text = Decode([], out var encoding);

        Assert.Equal(string.Empty, text);
        Assert.NotNull(encoding);
    }

    [Fact]
    public void Pure_ascii_is_read_as_utf8()
    {
        var text = Decode("hello"u8.ToArray(), out var encoding);

        Assert.Equal("hello", text);
        Assert.Equal(65001, encoding.CodePage);
    }
}
