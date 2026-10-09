using Contexo.App.UserMessages;
using Contexo.Core.Abstractions;

namespace Contexo.App.Tests.UserMessages;

public sealed class ErrorTextTests
{
    [Theory]
    [InlineData(typeof(DocumentErrorCode))]
    [InlineData(typeof(FolderState))]
    [InlineData(typeof(ClientConnectionState))]
    public void Every_enum_value_has_a_distinct_plain_text(Type enumType)
    {
        // Guards against a new enum member being added without a translation.
        var texts = new List<string>();
        foreach (var value in Enum.GetValues(enumType))
        {
            var text = (string)typeof(ErrorText).GetMethod(nameof(ErrorText.ToText), [enumType])!.Invoke(null, [value])!;
            Assert.False(string.IsNullOrWhiteSpace(text), $"{enumType.Name}.{value} has no text");
            Assert.DoesNotContain(value.ToString()!, text);
            texts.Add(text);
        }

        Assert.Equal(texts.Count, texts.Distinct().Count());
    }

    [Fact]
    public void Well_known_phrases()
    {
        Assert.Equal("有密碼保護", ErrorText.ToText(DocumentErrorCode.PasswordProtected));
        Assert.Equal("正被其他程式開啟", ErrorText.ToText(DocumentErrorCode.Locked));
        Assert.Equal("無法存取", ErrorText.ToText(FolderState.Unavailable));
    }
}
