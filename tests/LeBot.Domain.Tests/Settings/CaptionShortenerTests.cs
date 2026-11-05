using LeBot.Domain.Settings;

namespace LeBot.Domain.Tests.Settings;

public class CaptionShortenerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n ")]
    [InlineData("#allparts #авто @someone")]
    public void Shorten_NothingButNoise_ReturnsNull(string? text)
    {
        CaptionShortener.Shorten(text).Should().BeNull();
    }

    [Fact]
    public void Shorten_StripsHashtagsAndMentions_KeepsTheWords()
    {
        var text = "Усі запчастини в одному місці! @all_parts.in.ua\n\n#allparts #автозапчастини";

        CaptionShortener.Shorten(text).Should().Be("Усі запчастини в одному місці!");
    }

    [Fact]
    public void Shorten_KeepsEmailAndInWordSymbols()
    {
        CaptionShortener.Shorten("Write to shop@example.com, C# rocks").Should().Be("Write to shop@example.com, C# rocks");
    }

    [Fact]
    public void Shorten_CollapsesBlankLinesAndSpaces()
    {
        CaptionShortener.Shorten("one   two\n\n\n  three ").Should().Be("one two\nthree");
    }

    [Fact]
    public void Shorten_LongText_CutsAtWordBoundaryWithEllipsis()
    {
        var text = string.Join(' ', Enumerable.Repeat("word", 100));

        var shortened = CaptionShortener.Shorten(text);

        shortened.Should().EndWith("word…");
        shortened!.Length.Should().BeLessThanOrEqualTo(CaptionShortener.MaxLength + 1);
    }

    [Fact]
    public void Shorten_LongUnbrokenText_CutsHard()
    {
        var shortened = CaptionShortener.Shorten(new string('a', 500));

        shortened.Should().Be(new string('a', CaptionShortener.MaxLength) + "…");
    }
}
