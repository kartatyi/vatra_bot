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

    // The post shares a reel: its own renditions are empty, the clip sits one level down under
    // text_post_app_info.linked_inline_media. Reading only the node made this look text-only.
    [Fact]
    public void FromHtml_InlineMediaPost_TakesTheClipAttachedToIt()
    {
        var post = ThreadsPostPayload.FromHtml(Fixture("inline-media-post.html"), "DbInlineReel");

        var clip = post!.Media.Should().ContainSingle().Subject;
        clip.Kind.Should().Be(MediaKind.Video);
        clip.Url.Should().Be("https://cdn.test/reel-progressive.mp4");
        post.IsTextOnly.Should().BeFalse();
    }

    [Fact]
    public void FromHtml_InlineMediaPost_KeepsTheAuthorsOwnWords()
    {
        var post = ThreadsPostPayload.FromHtml(Fixture("inline-media-post.html"), "DbInlineReel");

        post!.Author.Should().Be("nissalorraine");
        post.Caption.Should().Be("😍");
    }

    [Fact]
    public void FromHtml_QuotedPost_TakesTheQuotedPhoto()
    {
        var post = ThreadsPostPayload.FromHtml(Fixture("quoted-post.html"), "DbQuoteTop");

        var photo = post!.Media.Should().ContainSingle().Subject;
        photo.Kind.Should().Be(MediaKind.Photo);
        photo.Url.Should().Be("https://cdn.test/quoted-1080.jpg");
    }

    [Fact]
    public void FromHtml_QuotedPost_PutsTheQuotedWordsUnderTheAuthorsOwn()
    {
        var post = ThreadsPostPayload.FromHtml(Fixture("quoted-post.html"), "DbQuoteTop");

        post!.Author.Should().Be("commentator");
        post.Caption.Should().Be("this aged well\n\n↩ @original.poster: bold prediction for 2026");
    }

    // A quote of a text post is still text — the card renders both halves — so it must not be
    // reported as an attachment the parser couldn't read.
    [Fact]
    public void FromHtml_QuoteOfATextPost_IsTextOnly()
    {
        var html = PageWith(
            """
            {"code":"DbQuoteText","user":{"username":"u"},"caption":{"text":"lol"},"media_type":19,
             "image_versions2":{"candidates":[]},
             "text_post_app_info":{"share_info":{"quoted_post":{"code":"DbQuotedTxt","user":{"username":"v"},
               "caption":{"text":"hot take"},"media_type":19,"image_versions2":{"candidates":[]}}}}}
            """);

        var post = ThreadsPostPayload.FromHtml(html, "DbQuoteText");

        post!.IsTextOnly.Should().BeTrue();
        post.Caption.Should().Be("lol\n\n↩ @v: hot take");
    }

    [Fact]
    public void FromHtml_TextOnlyPost_ReturnsNoMediaSoTheCardCanServeIt()
    {
        var post = ThreadsPostPayload.FromHtml(Fixture("text-post.html"), "DbTextOnly1");

        post!.Media.Should().BeEmpty();
        post.IsTextOnly.Should().BeTrue();
        post.Caption.Should().Be("no pictures, just opinions");
    }

    // A GIF from the Giphy picker: the parser has no reader for it, and the post is not text —
    // the honest answer is "here is something I can't read", never "text-only".
    [Fact]
    public void FromHtml_UnreadableAttachment_IsNeitherMediaNorTextOnly()
    {
        var post = ThreadsPostPayload.FromHtml(Fixture("unreadable-attachment-post.html"), "DbGifOnly01");

        post!.Media.Should().BeEmpty();
        post.UnreadAttachments.Should().Equal("giphy_media_info");
        post.IsTextOnly.Should().BeFalse();
    }

    // The node says it is a video post and lists no renditions. Whatever Meta did there, the card
    // would be a poster frame — name the claim so the extractor refuses it.
    [Fact]
    public void FromHtml_NodeClaimingMediaItDoesNotCarry_NamesTheClaim()
    {
        var html = PageWith(
            """
            {"code":"DbClaimsVid","user":{"username":"u"},"caption":{"text":"t"},"media_type":2,
             "carousel_media":null,"image_versions2":{"candidates":[]},"video_versions":null}
            """);

        var post = ThreadsPostPayload.FromHtml(html, "DbClaimsVid");

        post!.UnreadAttachments.Should().Equal("media_type 2");
        post.IsTextOnly.Should().BeFalse();
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
    public void FromHtml_ChainedPost_ReturnsTheAuthorsOwnPartsInOrder()
    {
        var post = ThreadsPostPayload.FromHtml(Fixture("chained-post.html"), "DbChainRoot");

        post!.Continuation.Select(part => part.Text).Should()
            .Equal("part two", "part three, with a picture", "part four");
    }

    // The chain and the comment section live in the same edges list. Everything from the first
    // stranger on is the comment section — including the author's own answers inside it.
    [Fact]
    public void FromHtml_ChainedPost_StopsWhereTheCommentsStart()
    {
        var post = ThreadsPostPayload.FromHtml(Fixture("chained-post.html"), "DbChainRoot");

        post!.Continuation.Should().NotContain(part =>
            part.Text!.Contains("comment") || part.Text.Contains("answering"));
    }

    [Fact]
    public void FromHtml_ChainedPost_KeepsMediaAttachedToItsOwnPart()
    {
        var post = ThreadsPostPayload.FromHtml(Fixture("chained-post.html"), "DbChainRoot");

        post!.Media.Should().ContainSingle().Which.Url.Should().Be("https://cdn.test/root-clip.mp4");
        post.Continuation[0].Media.Should().BeEmpty();
        post.Continuation[1].Media.Should().ContainSingle().Which.Url.Should().Be("https://cdn.test/part3.jpg");
        post.Continuation[2].Media.Should().BeEmpty();
    }

    [Fact]
    public void FromHtml_PostWithoutAChain_HasNoContinuation()
    {
        var post = ThreadsPostPayload.FromHtml(Fixture("carousel-post.html"), "DbsHKtBiGxC");

        post!.Continuation.Should().BeEmpty();
    }

    [Fact]
    public void FromJson_BlockLiftedOutOfTheBrowser_ParsesTheSameWay()
    {
        var block = ExtractFirstJsonBlock(Fixture("video-post.html"));

        var post = ThreadsPostPayload.FromJson(block, "DbnWztIiMQa");

        post!.Media.Should().ContainSingle().Which.Kind.Should().Be(MediaKind.Video);
    }

    private static string PageWith(string postJson) =>
        """<script type="application/json">{"data":{"media":""" + postJson + "}}</script>";

    private static string ExtractFirstJsonBlock(string html)
    {
        var start = html.IndexOf('{', html.IndexOf("<script", StringComparison.Ordinal));
        var end = html.IndexOf("</script>", start, StringComparison.Ordinal);
        return html[start..end];
    }

    private static string Fixture(string fileName) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "_fixtures", "threads", fileName));
}
