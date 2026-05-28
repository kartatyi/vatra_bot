using LeBot.Infrastructure.Text;

namespace LeBot.Infrastructure.Tests.Text;

public class RegexUrlExtractorTests
{
    private readonly RegexUrlExtractor _sut = new();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("just text no urls")]
    [InlineData("not.a.url someword")]
    public void Extract_NoUrls_ReturnsEmpty(string text)
    {
        _sut.Extract(text).Should().BeEmpty();
    }

    [Theory]
    [InlineData("https://tiktok.com/@user/video/123")]
    [InlineData("http://example.com")]
    [InlineData("https://example.com/path?query=1&foo=bar")]
    public void Extract_SingleUrl_ReturnsIt(string url)
    {
        var result = _sut.Extract($"look at {url} cool");

        result.Should().ContainSingle()
            .Which.AbsoluteUri.Should().Be(new Uri(url).AbsoluteUri);
    }

    [Fact]
    public void Extract_MultipleUrls_AreReturnedInOrder()
    {
        var text = "first https://a.example.com/ second https://b.example.com/ done";

        var result = _sut.Extract(text);

        result.Should().HaveCount(2);
        result[0].Host.Should().Be("a.example.com");
        result[1].Host.Should().Be("b.example.com");
    }

    [Theory]
    [InlineData("look at https://example.com.", "https://example.com/")]
    [InlineData("see https://example.com)", "https://example.com/")]
    [InlineData("crazy https://example.com!", "https://example.com/")]
    [InlineData("quote \"https://example.com\"", "https://example.com/")]
    [InlineData("paren (https://example.com)", "https://example.com/")]
    public void Extract_StripsTrailingPunctuation(string text, string expectedAbsoluteUri)
    {
        var result = _sut.Extract(text);

        result.Should().ContainSingle()
            .Which.AbsoluteUri.Should().Be(expectedAbsoluteUri);
    }

    [Fact]
    public void Extract_DuplicateUrls_AreDeduplicated()
    {
        var text = "https://example.com/x and again https://example.com/x";

        _sut.Extract(text).Should().HaveCount(1);
    }

    [Theory]
    [InlineData("link ftp://example.com/file")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    public void Extract_NonHttpSchemes_AreFiltered(string text)
    {
        _sut.Extract(text).Should().BeEmpty();
    }

    [Fact]
    public void Extract_UrlAtStartOfString_IsFound()
    {
        var result = _sut.Extract("https://example.com/ is cool");

        result.Should().ContainSingle()
            .Which.AbsoluteUri.Should().Be("https://example.com/");
    }
}
