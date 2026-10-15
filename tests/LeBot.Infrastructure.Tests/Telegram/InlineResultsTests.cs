using LeBot.Application.UseCases.AnswerInlineQuery;
using LeBot.Domain.Media;
using LeBot.Infrastructure.Telegram;
using Telegram.Bot.Types.InlineQueryResults;

namespace LeBot.Infrastructure.Tests.Telegram;

public class InlineResultsTests
{
    private static readonly Uri Url = new("https://www.tiktok.com/@a/video/1");

    [Fact]
    public void For_ReadyVideoWithText_OffersTwoTitledPlaceholders()
    {
        var (results, cache) = InlineResults.For(new InlineAnswer.Ready(new PreparedPost(MediaKind.Video, "f", "the post"), Url));

        var articles = results.Should().AllBeOfType<InlineQueryResultArticle>().Subject.ToList();
        articles.Select(a => a.Title).Should().Equal("📹 Video", "📹 Video + text");
        articles.Select(a => a.Description).Should().Equal(InlineResults.WithoutTextDescription, "the post");
        cache.Should().Be(0);
    }

    [Fact]
    public void For_ReadyVideoWithoutText_OffersOnlyTheBareChoice()
    {
        var (results, _) = InlineResults.For(new InlineAnswer.Ready(new PreparedPost(MediaKind.Video, "f", null), Url));

        results.Should().ContainSingle().Which.Should().BeOfType<InlineQueryResultArticle>().Which.Title.Should().Be("📹 Video");
    }

    [Fact]
    public void For_ReadyPhoto_UsesPhotoResultsAndLabels()
    {
        var (results, cache) = InlineResults.For(new InlineAnswer.Ready(new PreparedPost(MediaKind.Photo, "p", "listing"), Url));

        // Titled placeholders, not photo results — clients show photo results without titles.
        var articles = results.Should().AllBeOfType<InlineQueryResultArticle>().Subject.ToList();
        articles.Select(a => a.Title).Should().Equal("🖼 Photo", "🖼 Photo + text");
        articles[0].Description.Should().Be(InlineResults.WithoutTextDescription);
        InlineResults.TryParsePending(articles[0].Id, out var bareWithText).Should().BeTrue();
        InlineResults.TryParsePending(articles[1].Id, out var textWithText).Should().BeTrue();
        (bareWithText, textWithText).Should().Be((false, true));
        articles[0].ReplyMarkup.Should().NotBeNull();
        cache.Should().Be(0);
    }

    [Fact]
    public void For_ReadyTextOnly_OffersOneTextArticle()
    {
        var (results, _) = InlineResults.For(new InlineAnswer.Ready(new PreparedPost(null, null, "words"), Url));

        var article = results.Should().ContainSingle().Which.Should().BeOfType<InlineQueryResultArticle>().Subject;
        article.Title.Should().Be("📝 Text");
        article.InputMessageContent.Should().BeOfType<InputTextMessageContent>().Which.MessageText.Should().Be("words");
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
