using LeBot.Application.UseCases.AnswerInlineQuery;
using LeBot.Domain.Media;
using LeBot.Infrastructure.Telegram;
using Telegram.Bot.Types.InlineQueryResults;

namespace LeBot.Infrastructure.Tests.Telegram;

public class InlineResultsTests
{
    private static readonly Uri Url = new("https://www.tiktok.com/@a/video/1");

    [Fact]
    public void For_ReadyVideoWithText_OffersBareAndCaptioned()
    {
        var (results, cache) = InlineResults.For(new InlineAnswer.Ready(new PreparedPost(MediaKind.Video, "f", "the post")));

        results.Should().HaveCount(2);
        var bare = results[0].Should().BeOfType<InlineQueryResultCachedVideo>().Subject;
        var captioned = results[1].Should().BeOfType<InlineQueryResultCachedVideo>().Subject;
        (bare.Title, bare.Caption, bare.VideoFileId).Should().Be(("📹 Video", null, "f"));
        (captioned.Title, captioned.Caption).Should().Be(("📹 Video + text", "the post"));
        cache.Should().BePositive();
    }

    [Fact]
    public void For_ReadyVideoWithoutText_OffersOnlyTheBareVideo()
    {
        var (results, _) = InlineResults.For(new InlineAnswer.Ready(new PreparedPost(MediaKind.Video, "f", null)));

        results.Should().ContainSingle().Which.Should().BeOfType<InlineQueryResultCachedVideo>();
    }

    [Fact]
    public void For_ReadyPhoto_UsesPhotoResultsAndLabels()
    {
        var (results, _) = InlineResults.For(new InlineAnswer.Ready(new PreparedPost(MediaKind.Photo, "p", "listing")));

        results.Should().AllBeOfType<InlineQueryResultCachedPhoto>();
        results.Cast<InlineQueryResultCachedPhoto>().Select(r => r.Title).Should().Equal("🖼 Photo", "🖼 Photo + text");
    }

    [Fact]
    public void For_ReadyTextOnly_OffersOneTextArticle()
    {
        var (results, _) = InlineResults.For(new InlineAnswer.Ready(new PreparedPost(null, null, "words")));

        var article = results.Should().ContainSingle().Which.Should().BeOfType<InlineQueryResultArticle>().Subject;
        article.Title.Should().Be("📝 Text");
        article.InputMessageContent.Should().BeOfType<InputTextMessageContent>().Which.MessageText.Should().Be("words");
    }

    [Fact]
    public void For_LongCaption_IsCutToTelegramsLimit()
    {
        var (results, _) = InlineResults.For(new InlineAnswer.Ready(new PreparedPost(MediaKind.Video, "f", new string('a', 2000))));

        results[1].Should().BeOfType<InlineQueryResultCachedVideo>().Which.Caption!.Length.Should().Be(1024);
    }

    [Theory]
    [InlineData(false, InlineResults.NotReadyTitle)]
    [InlineData(true, InlineResults.FailedTitle)]
    public void For_NotReadyOrFailed_OffersTheLinkItself_Uncached(bool failed, string title)
    {
        InlineAnswer answer = failed ? new InlineAnswer.Failed(Url) : new InlineAnswer.NotReady(Url);

        var (results, cache) = InlineResults.For(answer);

        var article = results.Should().ContainSingle().Which.Should().BeOfType<InlineQueryResultArticle>().Subject;
        article.Title.Should().Be(title);
        article.InputMessageContent.Should().BeOfType<InputTextMessageContent>().Which.MessageText.Should().Be(Url.AbsoluteUri);
        cache.Should().Be(0);
    }

    [Fact]
    public void For_NoLink_OffersNothing()
    {
        InlineResults.For(new InlineAnswer.NoLink()).Results.Should().BeEmpty();
    }
}
