using LeBot.Infrastructure.MediaExtraction.Instagram;

namespace LeBot.Infrastructure.Tests.MediaExtraction.Instagram;

public class InstagramMediaIdTests
{
    [Theory]
    [InlineData("B", "1")]
    [InlineData("C", "2")]
    [InlineData("-", "62")]
    [InlineData("_", "63")]
    [InlineData("BA", "64")]
    [InlineData("10", "3444")]
    // Real carousel shortcode -> media pk, verified against Instagram's own api/v1 response.
    [InlineData("DaGKRNaCLiX", "3928872888018974871")]
    public void FromShortcode_ValidShortcode_DecodesToMediaId(string shortcode, string expected)
    {
        InstagramMediaId.FromShortcode(shortcode).Should().Be(expected);
    }

    [Theory]
    [InlineData("abc!")]
    [InlineData("has space")]
    [InlineData("with/slash")]
    public void FromShortcode_CharOutsideAlphabet_ReturnsNull(string shortcode)
    {
        InstagramMediaId.FromShortcode(shortcode).Should().BeNull();
    }

    [Fact]
    public void FromShortcode_Empty_ReturnsNull()
    {
        InstagramMediaId.FromShortcode(string.Empty).Should().BeNull();
    }
}
