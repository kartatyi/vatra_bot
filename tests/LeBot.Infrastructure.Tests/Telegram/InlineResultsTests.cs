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

        results.Should().HaveCount(3);
        var bare = results[0].Should().BeOfType<InlineQueryResultCachedVideo>().Subject;
        var captioned = results[1].Should().BeOfType<InlineQueryResultCachedVideo>().Subject;
        var textOnly = results[2].Should().BeOfType<InlineQueryResultArticle>().Subject;
        (bare.Title, bare.Caption, bare.VideoFileId, bare.Description).Should().Be(("📹 Video", null, "f", InlineResults.WithoutTextDescription));
        (captioned.Title, captioned.Caption).Should().Be(("📹 Video + text", "the post"));
        textOnly.Title.Should().Be(InlineResults.TextOnlyTitle);
        textOnly.InputMessageContent.Should().BeOfType<InputTextMessageContent>().Which.MessageText.Should().Be("the post");
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

        // Not all photos: the text-only article is what makes clients list the choices instead of a title-less grid.
        results.Should().NotBeEmpty().And.Contain(r => r is InlineQueryResultArticle);
        results.OfType<InlineQueryResultCachedPhoto>().Select(r => r.Title).Should().Equal("🖼 Photo", "🖼 Photo + text");
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

    [Fact]
    public void For_Failed_OffersTheLinkItself_Uncached()
    {
        var (results, cache) = InlineResults.For(new InlineAnswer.Failed(Url));

        var article = results.Should().ContainSingle().Which.Should().BeOfType<InlineQueryResultArticle>().Subject;
        article.Title.Should().Be(InlineResults.FailedTitle);
        article.InputMessageContent.Should().BeOfType<InputTextMessageContent>().Which.MessageText.Should().Be(Url.AbsoluteUri);
        cache.Should().Be(0);
    }

    [Fact]
    public void For_NotReady_OffersTwoPlaceholdersWithTheOriginalLink_Uncached()
    {
        var (results, cache) = InlineResults.For(new InlineAnswer.NotReady(Url));

        var articles = results.Should().AllBeOfType<InlineQueryResultArticle>().Subject.ToList();
        articles.Select(a => a.Title).Should().Equal(InlineResults.PendingTitle, InlineResults.PendingWithTextTitle);
        articles.Should().AllSatisfy(a =>
        {
            a.InputMessageContent.Should().BeOfType<InputTextMessageContent>()
                .Which.MessageText.Should().Be($"{InlineResults.LoadingText}\n{Url.AbsoluteUri}");
            // Without a keyboard Telegram never reports the inline message id, and the placeholder can't be edited.
            a.ReplyMarkup!.InlineKeyboard.Single().Single().Url.Should().Be(Url.AbsoluteUri);
        });
        cache.Should().Be(0);
    }

    [Theory]
    [InlineData("pending:bare", true, false)]
    [InlineData("pending:text", true, true)]
    [InlineData("bare", false, false)]
    [InlineData("failed", false, false)]
    public void TryParsePending_RecognisesOnlyThePlaceholders(string resultId, bool pending, bool withText)
    {
        InlineResults.TryParsePending(resultId, out var parsedWithText).Should().Be(pending);
        if (pending)
        {
            parsedWithText.Should().Be(withText);
        }
    }

    [Fact]
    public void For_NoLink_OffersNothing()
    {
        InlineResults.For(new InlineAnswer.NoLink()).Results.Should().BeEmpty();
    }
}
