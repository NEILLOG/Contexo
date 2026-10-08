using System.Globalization;
using System.Text;

namespace Contexo.Core.Embedding;

/// <summary>
/// BERT BasicTokenizer + WordPiece, matching the Hugging Face <c>BertTokenizer</c> behaviour
/// (clean text, isolate CJK characters, split punctuation, optional lowercase + accent stripping, longest-match WordPiece).
/// Output is <c>[CLS] … [SEP]</c>, truncated to <c>maxTokens</c>. Instances are immutable and thread-safe.
/// </summary>
/// <remarks>
/// Implemented by hand rather than with <c>Microsoft.ML.Tokenizers.BertTokenizer</c> so the CJK / punctuation / truncation rules
/// are exactly the ones documented in the task and independent of library defaults.
/// </remarks>
internal sealed class WordPieceTokenizer
{
    public const string ClsToken = "[CLS]";
    public const string SepToken = "[SEP]";
    public const string UnkToken = "[UNK]";
    public const string ContinuationPrefix = "##";
    public const int MaxCharsPerWord = 100;

    private readonly IReadOnlyDictionary<string, int> _vocab;
    private readonly bool _lowercase;
    private readonly int _maxTokens;

    public WordPieceTokenizer(IReadOnlyDictionary<string, int> vocab, bool lowercase, int maxTokens)
    {
        ArgumentNullException.ThrowIfNull(vocab);
        if (maxTokens < 3)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTokens), "maxTokens must leave room for [CLS] and [SEP]");
        }

        foreach (var special in new[] { ClsToken, SepToken, UnkToken })
        {
            if (!vocab.ContainsKey(special))
            {
                throw new InvalidDataException($"Vocabulary does not contain {special}");
            }
        }

        _vocab = vocab;
        _lowercase = lowercase;
        _maxTokens = maxTokens;
        ClsId = vocab[ClsToken];
        SepId = vocab[SepToken];
        UnkId = vocab[UnkToken];
    }

    public int ClsId { get; }

    public int SepId { get; }

    public int UnkId { get; }

    /// <summary>Reads a one-token-per-line vocab file; the line number (from 0) is the id.</summary>
    public static Dictionary<string, int> LoadVocab(string path)
    {
        var vocab = new Dictionary<string, int>(StringComparer.Ordinal);
        var id = 0;
        foreach (var raw in File.ReadLines(path, Encoding.UTF8))
        {
            var token = raw.TrimEnd('\r', '\n');
            vocab.TryAdd(token, id);
            id++;
        }

        return vocab;
    }

    /// <summary>Tokenizes text into <c>[CLS] … [SEP]</c> ids, never longer than <c>maxTokens</c>.</summary>
    public int[] Encode(string text) => Encode(text, out _);

    /// <summary>Same as <see cref="Encode(string)"/> but also returns the token strings (for tests and diagnostics).</summary>
    public int[] Encode(string text, out List<string> tokens)
    {
        ArgumentNullException.ThrowIfNull(text);

        tokens = [ClsToken];
        var ids = new List<int>(Math.Min(_maxTokens, text.Length + 2)) { ClsId };
        var budget = _maxTokens - 2;
        var produced = 0;
        var pieces = new List<(string Token, int Id)>();

        foreach (var word in BasicTokenize(text))
        {
            pieces.Clear();
            WordPiece(word, pieces);
            foreach (var (token, id) in pieces)
            {
                if (produced >= budget)
                {
                    goto Done;
                }

                tokens.Add(token);
                ids.Add(id);
                produced++;
            }
        }

    Done:
        tokens.Add(SepToken);
        ids.Add(SepId);
        return [.. ids];
    }

    /// <summary>BERT BasicTokenizer: yields words (CJK characters and punctuation each as a separate word).</summary>
    internal IEnumerable<string> BasicTokenize(string text)
    {
        var cleaned = CleanAndIsolateCjk(text);
        foreach (var chunk in cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var word = chunk;
            if (_lowercase)
            {
                word = StripAccents(word.ToLowerInvariant());
            }

            foreach (var part in SplitOnPunctuation(word))
            {
                yield return part;
            }
        }
    }

    private static string CleanAndIsolateCjk(string text)
    {
        var sb = new StringBuilder(text.Length + 16);
        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.Value;
            if (value == 0 || value == 0xFFFD || IsControl(rune))
            {
                continue;
            }

            if (Rune.IsWhiteSpace(rune))
            {
                sb.Append(' ');
            }
            else if (IsCjk(value))
            {
                sb.Append(' ').Append(rune.ToString()).Append(' ');
            }
            else
            {
                sb.Append(rune.ToString());
            }
        }

        return sb.ToString();
    }

    private static string StripAccents(string word)
    {
        if (word.All(char.IsAscii))
        {
            return word;
        }

        var decomposed = word.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var rune in decomposed.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(rune.ToString());
            }
        }

        return sb.ToString();
    }

    private static IEnumerable<string> SplitOnPunctuation(string word)
    {
        var current = new StringBuilder();
        foreach (var rune in word.EnumerateRunes())
        {
            if (IsPunctuation(rune))
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }

                yield return rune.ToString();
            }
            else
            {
                current.Append(rune.ToString());
            }
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    private void WordPiece(string word, List<(string Token, int Id)> output)
    {
        // Work in UTF-16 code units but never split a surrogate pair.
        if (word.Length > MaxCharsPerWord)
        {
            output.Add((UnkToken, UnkId));
            return;
        }

        var start = 0;
        var found = new List<(string Token, int Id)>();
        while (start < word.Length)
        {
            var end = word.Length;
            (string Token, int Id)? match = null;
            while (end > start)
            {
                if (end < word.Length && char.IsLowSurrogate(word[end]))
                {
                    end--;
                    continue;
                }

                var piece = word.Substring(start, end - start);
                if (start > 0)
                {
                    piece = ContinuationPrefix + piece;
                }

                if (_vocab.TryGetValue(piece, out var id))
                {
                    match = (piece, id);
                    break;
                }

                end--;
            }

            if (match is null)
            {
                output.Add((UnkToken, UnkId));
                return;
            }

            found.Add(match.Value);
            start = end;
        }

        output.AddRange(found);
    }

    private static bool IsControl(Rune rune)
    {
        if (rune.Value is '\t' or '\n' or '\r')
        {
            return false;
        }

        var category = Rune.GetUnicodeCategory(rune);
        return category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate
            or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned;
    }

    private static bool IsPunctuation(Rune rune)
    {
        var value = rune.Value;
        if ((value >= 33 && value <= 47) || (value >= 58 && value <= 64) || (value >= 91 && value <= 96) || (value >= 123 && value <= 126))
        {
            return true;
        }

        return Rune.GetUnicodeCategory(rune) is UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation
            or UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation or UnicodeCategory.InitialQuotePunctuation
            or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.OtherPunctuation;
    }

    internal static bool IsCjk(int codePoint) =>
        (codePoint >= 0x4E00 && codePoint <= 0x9FFF)
        || (codePoint >= 0x3400 && codePoint <= 0x4DBF)
        || (codePoint >= 0x20000 && codePoint <= 0x2A6DF)
        || (codePoint >= 0x2A700 && codePoint <= 0x2B73F)
        || (codePoint >= 0x2B740 && codePoint <= 0x2B81F)
        || (codePoint >= 0x2B820 && codePoint <= 0x2CEAF)
        || (codePoint >= 0xF900 && codePoint <= 0xFAFF)
        || (codePoint >= 0x2F800 && codePoint <= 0x2FA1F);
}
