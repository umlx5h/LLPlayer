using FluentAssertions;

namespace FlyleafLib;

public class SubtitleTextUtilTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("   ", "   ")] // Assume trim is done beforehand
    [InlineData("Hello", "Hello")]
    [InlineData("Hello\nWorld", "Hello World")]
    [InlineData("Hello\r\nWorld", "Hello World")]
    [InlineData("Hello\n\nWorld", "Hello World")]
    [InlineData("Hello\r\n\r\nWorld", "Hello World")]
    [InlineData("Hello\n  \nWorld", "Hello    World")]

    [InlineData("- Hello\n- How are you?", "- Hello\n- How are you?")]
    [InlineData("- Hello\n - How are you?", "- Hello  - How are you?")]
    [InlineData("- Hello\r- How are you?", "- Hello\r- How are you?")]
    [InlineData("- Hello\n\n- How are you?", "- Hello\n\n- How are you?")]
    [InlineData("- Hello\r\n- How are you?", "- Hello\r\n- How are you?")]
    [InlineData("- Hello\nWorld", "- Hello World")]
    [InlineData("- こんにちは\n- 世界", "- こんにちは\n- 世界")]
    [InlineData("- こんにちは\n世界", "- こんにちは 世界")]

    [InlineData("こんにちは\n世界", "こんにちは 世界")]
    [InlineData("🙂\n🙃", "🙂 🙃")]
    [InlineData("<i>Hello</i>\n<i>World</i>", "<i>Hello</i> <i>World</i>")]

    [InlineData("- Hello\n- Good\nbye", "- Hello\n- Good bye")]
    [InlineData("- Hello\nWorld\n- Good\nbye", "- Hello World\n- Good bye")]

    [InlineData("Hello\n- Good\n- bye", "Hello - Good - bye")]
    [InlineData(" -Hello\n- Good\n- bye", " -Hello - Good - bye")]

    [InlineData("- Hello\n- aa-bb-cc dd", "- Hello\n- aa-bb-cc dd")]
    [InlineData("- Hello\naa-bb-cc dd", "- Hello aa-bb-cc dd")]

    [InlineData("- Hello\n- Goodbye", "- Hello\n- Goodbye")] // hyphen
    [InlineData("– Hello\n– Goodbye", "– Hello\n– Goodbye")] // en dash
    [InlineData("- Hello\n– Goodbye", "- Hello – Goodbye")] // hyphen + en dash

    public void FlattenUnlessAllDash_ShouldReturnExpected(string input, string expected)
    {
        string result = SubtitleTextUtil.FlattenText(input);
        result.Should().Be(expected);
    }
}
