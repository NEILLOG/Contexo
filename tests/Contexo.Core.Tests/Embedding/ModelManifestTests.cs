using Contexo.Core.Embedding;
using Contexo.Core.Tests.Common;

namespace Contexo.Core.Tests.Embedding;

public sealed class ModelManifestTests
{
    private const string Valid = """
        {
          "id": "bge-small-zh-v1.5/int8",
          "tokenizer": "wordpiece",
          "vocab": "vocab.txt",
          "lowercase": true,
          "maxTokens": 512,
          "dimensions": 512,
          "pooling": "cls",
          "normalize": true,
          "queryPrefix": "为这个句子生成表示以用于检索相关文章：",
          "passagePrefix": "",
          "inputs": { "ids": "input_ids", "mask": "attention_mask", "typeIds": "token_type_ids" },
          "output": "last_hidden_state"
        }
        """;

    [Fact]
    public void Parse_reads_every_field()
    {
        var manifest = ModelManifest.Parse(Valid);

        Assert.Equal("bge-small-zh-v1.5/int8", manifest.Id);
        Assert.Equal("wordpiece", manifest.Tokenizer);
        Assert.Equal("vocab.txt", manifest.Vocab);
        Assert.True(manifest.Lowercase);
        Assert.Equal(512, manifest.MaxTokens);
        Assert.Equal(512, manifest.Dimensions);
        Assert.Equal("cls", manifest.Pooling);
        Assert.True(manifest.Normalize);
        Assert.Equal("为这个句子生成表示以用于检索相关文章：", manifest.QueryPrefix);
        Assert.Equal("", manifest.PassagePrefix);
        Assert.Equal(new ModelInputNames("input_ids", "attention_mask", "token_type_ids"), manifest.Inputs);
        Assert.Equal("last_hidden_state", manifest.Output);
        Assert.Equal("model.onnx", manifest.ModelFile);
    }

    [Fact]
    public void Optional_fields_have_defaults_and_type_ids_may_be_missing()
    {
        var json = """
            {
              "id": "m", "tokenizer": "WordPiece", "vocab": "v.txt", "maxTokens": 128, "dimensions": 384, "pooling": "Mean",
              "inputs": { "ids": "input_ids", "mask": "attention_mask" },
              "output": "out"
            }
            """;

        var manifest = ModelManifest.Parse(json);

        Assert.Equal("wordpiece", manifest.Tokenizer);
        Assert.Equal("mean", manifest.Pooling);
        Assert.True(manifest.Lowercase);
        Assert.True(manifest.Normalize);
        Assert.Equal("", manifest.QueryPrefix);
        Assert.Null(manifest.Inputs.TypeIds);
    }

    [Theory]
    [InlineData("\"id\": \"bge-small-zh-v1.5/int8\",", "'id'")]
    [InlineData("\"tokenizer\": \"wordpiece\",", "'tokenizer'")]
    [InlineData("\"vocab\": \"vocab.txt\",", "'vocab'")]
    [InlineData("\"maxTokens\": 512,", "'maxTokens'")]
    [InlineData("\"dimensions\": 512,", "'dimensions'")]
    [InlineData("\"pooling\": \"cls\",", "'pooling'")]
    [InlineData("\"output\": \"last_hidden_state\"", "'output'")]
    public void Missing_required_field_names_the_field(string line, string expected)
    {
        var json = Valid.Replace(line, "");

        var ex = Assert.Throws<ModelManifestException>(() => ModelManifest.Parse(json));

        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Missing_inputs_object_is_reported()
    {
        var json = Valid.Replace("\"inputs\": { \"ids\": \"input_ids\", \"mask\": \"attention_mask\", \"typeIds\": \"token_type_ids\" },", "");

        var ex = Assert.Throws<ModelManifestException>(() => ModelManifest.Parse(json));

        Assert.Contains("inputs", ex.Message);
    }

    [Fact]
    public void Missing_input_name_is_reported()
    {
        var json = Valid.Replace("\"mask\": \"attention_mask\", ", "");

        var ex = Assert.Throws<ModelManifestException>(() => ModelManifest.Parse(json));

        Assert.Contains("inputs.mask", ex.Message);
    }

    [Fact]
    public void Unsupported_pooling_is_rejected()
    {
        var ex = Assert.Throws<ModelManifestException>(() => ModelManifest.Parse(Valid.Replace("\"cls\"", "\"max\"")));

        Assert.Contains("pooling", ex.Message);
    }

    [Fact]
    public void Non_positive_sizes_are_rejected()
    {
        Assert.Throws<ModelManifestException>(() => ModelManifest.Parse(Valid.Replace("\"dimensions\": 512", "\"dimensions\": 0")));
        Assert.Throws<ModelManifestException>(() => ModelManifest.Parse(Valid.Replace("\"maxTokens\": 512", "\"maxTokens\": 2")));
    }

    [Fact]
    public void Invalid_json_and_non_object_are_reported()
    {
        Assert.Contains("not valid JSON", Assert.Throws<ModelManifestException>(() => ModelManifest.Parse("{ nope")).Message);
        Assert.Throws<ModelManifestException>(() => ModelManifest.Parse("[1,2]"));
    }

    [Fact]
    public void Load_reads_file_and_reports_missing_file()
    {
        using var temp = new TempDirectory();

        Assert.Contains(ModelManifest.FileName, Assert.Throws<ModelManifestException>(() => ModelManifest.Load(temp.Path)).Message);

        File.WriteAllText(temp.Combine(ModelManifest.FileName), Valid);
        Assert.Equal("bge-small-zh-v1.5/int8", ModelManifest.Load(temp.Path).Id);
    }
}
