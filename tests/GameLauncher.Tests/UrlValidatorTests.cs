using GameLauncher.Core;

namespace GameLauncher.Tests;

public class UrlValidatorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_input_is_allowed(string? input)
    {
        Assert.Equal(UrlCheckState.Empty, UrlValidator.Validate(input).State);
    }

    [Theory]
    [InlineData("https://www.bilibili.com/video/BV1xx411c7mD")]
    [InlineData("http://example.com/a?b=c")]
    [InlineData("  https://example.com  ")]
    public void Http_and_https_urls_are_valid(string input)
    {
        var result = UrlValidator.Validate(input);
        Assert.Equal(UrlCheckState.Valid, result.State);
        Assert.NotNull(result.Url);
    }

    [Theory]
    [InlineData("www.bilibili.com/video/x")]
    [InlineData("ftp://example.com/file")]
    [InlineData("file:///C:/Windows/notepad.exe")]
    [InlineData("javascript://alert(1)")]
    [InlineData("https://user:pass@example.com/")]
    [InlineData("https://exa mple.com/")]
    [InlineData("https://")]
    public void Other_inputs_are_rejected_with_message(string input)
    {
        var result = UrlValidator.Validate(input);
        Assert.Equal(UrlCheckState.Invalid, result.State);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }
}
