using LeBot.Domain.Media;
using LeBot.Infrastructure.MediaExtraction.Threads;

namespace LeBot.Infrastructure.Tests.MediaExtraction.Threads;

public class ThreadsPostPayloadTests
{
    [Fact]
    public void FromHtml_CarouselPost_ReturnsEveryAttachmentInOrder()
    {
        var post = ThreadsPostPayload.FromHtml(Fixture("carousel-post.html"), "DbsHKtBiGxC");

        post!.Media.Should().HaveCount(2);
        post.Media.Should().OnlyContain(m => m.Kind == MediaKind.Photo);
        post.Media[0].Url.Should().Be("https://cdn.test/first-1080.jpg");
        post.Media[1].Url.Should().Be("https://cdn.test/second-1080.jpg");
    }

    // The regression: the page also describes a clip from the recommendation feed, first in
    // document order. Matching on the post's own shortcode is what keeps it out.
    [Fact]
    public void FromHtml_CarouselPost_IgnoresMediaBelongingToOtherPosts()
    {
        var post = ThreadsPostPayload.FromHtml(Fixture("carousel-post.html"), "DbsHKtBiGxC");

        post!.Media.Should().NotContain(m => m.Url.Contains("reco"));
    }

    [Fact]
    public void FromHtml_CarouselPost_ReadsAuthorAndCaption()
    {
        var post = ThreadsPostPayload.FromHtml(Fixture("carousel-post.html"), "DbsHKtBiGxC");

        post!.Author.Should().Be("haryahahahah");
        post.Caption.Should().Be("Kinder Joy, One Piece edition");
    }

    // Video renditions carry no dimensions — just the same clip as type 101/102/103 — so the first
    // one, Meta's progressive MP4, is the one to take. The poster image must not win here.
    [Fact]
    public void FromHtml_VideoPost_TakesTheClipNotItsPosterFrame()
    {
        var post = ThreadsPostPayload.FromHtml(Fixture("video-post.html"), "DbnWztIiMQa");

        var clip = post!.Media.Should().ContainSingle().Subject;
        clip.Kind.Should().Be(MediaKind.Video);
        clip.Url.Should().Be("https://cdn.test/clip-progressive.mp4");
    }

    [Fact]
    public void FromHtml_ImageCandidates_PicksTheHighestResolutionOne()
    {
        var post = ThreadsPostPayload.FromHtml(Fixture("carousel-post.html"), "DbsHKtBiGxC");

        // The 1080-wide candidate is listed *after* the 720 one in the fixture, so order can't carry it.
        post!.Media[0].Url.Should().Be("https://cdn.test/first-1080.jpg");
    }

    [Fact]
    public void FromHtml_TextOnlyPost_ReturnsNoMediaSoTheCardCanServeIt()
    {
        var post = ThreadsPostPayload.FromHtml(Fixture("text-post.html"), "DbTextOnly1");

        post!.Media.Should().BeEmpty();
        post.Caption.Should().Be("no pictures, just opinions");
    }

    [Fact]
    public void FromHtml_ShortcodeAbsentFromPage_ReturnsNull()
    {
        var post = ThreadsPostPayload.FromHtml(Fixture("carousel-post.html"), "SomeOtherCode");

        post.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html><body>logged-out shell</body></html>")]
    [InlineData("""<script type="application/json">{"code":"DbsHKtBiGxC"}</script>""")] // no media on the node
    public void FromHtml_PageWithoutAPayload_ReturnsNull(string html)
    {
        ThreadsPostPayload.FromHtml(html, "DbsHKtBiGxC").Should().BeNull();
    }

    [Fact]
    public void FromJson_BlockLiftedOutOfTheBrowser_ParsesTheSameWay()
    {
        var block = ExtractFirstJsonBlock(Fixture("video-post.html"));

        var post = ThreadsPostPayload.FromJson(block, "DbnWztIiMQa");

        post!.Media.Should().ContainSingle().Which.Kind.Should().Be(MediaKind.Video);
    }

    private static string ExtractFirstJsonBlock(string html)
    {
        var start = html.IndexOf('{', html.IndexOf("<script", StringComparison.Ordinal));
        var end = html.IndexOf("</script>", start, StringComparison.Ordinal);
        return html[start..end];
    }

    private static string Fixture(string fileName) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "_fixtures", "threads", fileName));
}
