using Contexo.Core.Embedding;

namespace Contexo.Core.Tests.Embedding;

public sealed class WordPieceTokenizerTests
{
    private static readonly string[] Tokens =
    [
        "[PAD]", "[UNK]", "[CLS]", "[SEP]",
        "監", "視", "系", "統", "報", "價", "你", "好", "\U00020000",
        "ab", "##c", "-", "1", "##2", "##3",
        "hello", ",", "world", "!", "，", "！", "（", "）", "cafe", "x", "un", "##known",
        "。",
    ];

    private static WordPieceTokenizer Create(bool lowercase = true, int maxTokens = 512)
    {
        var vocab = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < Tokens.Length; i++)
        {
            vocab[Tokens[i]] = i;
        }

        return new WordPieceTokenizer(vocab, lowercase, maxTokens);
    }

    private static List<string> Tokenize(string text, bool lowercase = true)
    {
        Create(lowercase).Encode(text, out var tokens);
        return tokens;
    }

    [Fact]
    public void Mixed_cjk_ascii_and_digits_are_split_into_wordpieces()
    {
        Assert.Equal(
            ["[CLS]", "監", "視", "系", "統", "ab", "##c", "-", "1", "##2", "##3", "報", "價", "[SEP]"],
            Tokenize("監視系統ABC-123報價"));
    }

    [Fact]
    public void Ids_follow_vocabulary_line_numbers()
    {
        var tokenizer = Create();
        var ids = tokenizer.Encode("監視");

        Assert.Equal([2, 4, 5, 3], ids);
        Assert.Equal(2, tokenizer.ClsId);
        Assert.Equal(3, tokenizer.SepId);
        Assert.Equal(1, tokenizer.UnkId);
    }

    [Fact]
    public void Ascii_punctuation_is_split_and_text_is_lowercased()
    {
        Assert.Equal(["[CLS]", "hello", ",", "world", "!", "[SEP]"], Tokenize("Hello, World!"));
    }

    [Fact]
    public void Without_lowercase_uppercase_words_are_unknown()
    {
        Assert.Equal(["[CLS]", "[UNK]", "[SEP]"], Tokenize("Hello", lowercase: false));
    }

    [Fact]
    public void Fullwidth_punctuation_is_split()
    {
        Assert.Equal(["[CLS]", "你", "好", "，", "world", "！", "[SEP]"], Tokenize("你好，world！"));
        Assert.Equal(["[CLS]", "（", "你", "）", "。", "[SEP]"], Tokenize("（你）。"));
    }

    [Fact]
    public void Accents_are_stripped_when_lowercasing()
    {
        Assert.Equal(["[CLS]", "cafe", "[SEP]"], Tokenize("Café"));
    }

    [Fact]
    public void Emoji_becomes_unknown_without_swallowing_neighbours()
    {
        Assert.Equal(["[CLS]", "[UNK]", "[SEP]"], Tokenize("😀"));
        Assert.Equal(["[CLS]", "hello", "[UNK]", "world", "[SEP]"], Tokenize("hello 😀 world"));
    }

    [Fact]
    public void Word_that_cannot_be_matched_is_a_single_unknown()
    {
        // "un" + "##known" matches, but "unzzz" has no valid continuation so the whole word is [UNK].
        Assert.Equal(["[CLS]", "un", "##known", "[SEP]"], Tokenize("unknown"));
        Assert.Equal(["[CLS]", "[UNK]", "[SEP]"], Tokenize("unzzz"));
    }

    [Fact]
    public void Word_longer_than_100_characters_is_unknown()
    {
        Assert.Equal(["[CLS]", "[UNK]", "[SEP]"], Tokenize(new string('x', 101)));
        Assert.Equal(["[CLS]", "x", "[SEP]"], Tokenize("x"));
    }

    [Fact]
    public void Empty_and_whitespace_only_input_gives_only_special_tokens()
    {
        Assert.Equal(["[CLS]", "[SEP]"], Tokenize(""));
        Assert.Equal(["[CLS]", "[SEP]"], Tokenize("   \t\r\n　  "));
    }

    [Fact]
    public void Control_and_zero_width_characters_are_removed()
    {
        Assert.Equal(["[CLS]", "hello", "[SEP]"], Tokenize("hel\u0000l​o\u0007"));
    }

    [Fact]
    public void Cjk_extension_b_characters_are_isolated()
    {
        Assert.Equal(["[CLS]", "\U00020000", "你", "[SEP]"], Tokenize("\U00020000你"));
    }

    [Theory]
    [InlineData(0x4E00, true)]
    [InlineData(0x9FFF, true)]
    [InlineData(0x3400, true)]
    [InlineData(0x20000, true)]
    [InlineData(0x2B81F, true)]
    [InlineData(0x2CEAF, true)]
    [InlineData(0xF900, true)]
    [InlineData(0x2FA1F, true)]
    [InlineData(0x3042, false)]
    [InlineData(0x0041, false)]
    [InlineData(0xFF0C, false)]
    public void Cjk_ranges(int codePoint, bool expected) =>
        Assert.Equal(expected, WordPieceTokenizer.IsCjk(codePoint));

    [Fact]
    public void Long_input_is_truncated_to_max_tokens_and_still_ends_with_sep()
    {
        var tokenizer = Create();
        var ids = tokenizer.Encode(new string('你', 2000));

        Assert.Equal(512, ids.Length);
        Assert.Equal(tokenizer.ClsId, ids[0]);
        Assert.Equal(tokenizer.SepId, ids[^1]);
        Assert.All(ids[1..^1], id => Assert.Equal(10, id));
    }

    [Fact]
    public void Truncation_respects_a_custom_limit()
    {
        var ids = Create(maxTokens: 6).Encode("監視系統報價你好");

        Assert.Equal(6, ids.Length);
        Assert.Equal([2, 4, 5, 6, 7, 3], ids);
    }

    [Fact]
    public void Truncation_never_splits_surrogate_pairs()
    {
        var ids = Create(maxTokens: 4).Encode("\U00020000\U00020000\U00020000");

        Assert.Equal([2, 12, 12, 3], ids);
    }

    [Fact]
    public void Vocabulary_must_contain_special_tokens()
    {
        var vocab = new Dictionary<string, int> { ["a"] = 0 };

        Assert.Throws<InvalidDataException>(() => new WordPieceTokenizer(vocab, true, 512));
    }

    [Fact]
    public void LoadVocab_uses_line_numbers_as_ids()
    {
        var path = Path.Combine(Path.GetTempPath(), $"contexo-vocab-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(path, "[PAD]\n[UNK]\r\n[CLS]\n[SEP]\n你\n");

            var vocab = WordPieceTokenizer.LoadVocab(path);

            Assert.Equal(5, vocab.Count);
            Assert.Equal(1, vocab["[UNK]"]);
            Assert.Equal(4, vocab["你"]);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
