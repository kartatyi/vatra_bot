using LeBot.Domain.Media;

namespace LeBot.Domain.Tests.Media;

public class MediaPayloadTests
{
    [Fact]
    public void HasMedia_IsFalse_WhenItemsEmpty()
    {
        var payload = new MediaPayload(
            new Uri("https://example.com/"),
            Title: "t",
            Author: "a",
            Items: []);

        payload.HasMedia.Should().BeFalse();
    }

    [Fact]
    public void HasMedia_IsTrue_WhenAtLeastOneItem()
    {
        var item = new MediaItem("/tmp/x.mp4", MediaKind.Video, "video/mp4", 100, 5);
        var payload = new MediaPayload(
            new Uri("https://example.com/"),
            Title: null,
            Author: null,
            Items: [item]);

        payload.HasMedia.Should().BeTrue();
    }

    [Theory]
    [InlineData("post", "title", "me", "post|| me")]
    [InlineData(null, "title", "me", "title|| me")]
    [InlineData("  ", "title", null, "title")]
    [InlineData(null, null, "me", "— me")]
    public void Body_PrefersDescription_ThenTitle_ThenAppendsAuthor(string? description, string? title, string? author, string expected)
    {
        var payload = new MediaPayload(new Uri("https://example.com"), title, author, [], description);

        payload.Body.Should().Be(expected.Replace("|| ", "\n\n— ", StringComparison.Ordinal));
    }

    [Fact]
    public void Body_NothingToSay_IsNull()
    {
        new MediaPayload(new Uri("https://example.com"), " ", null, []).Body.Should().BeNull();
    }
}
