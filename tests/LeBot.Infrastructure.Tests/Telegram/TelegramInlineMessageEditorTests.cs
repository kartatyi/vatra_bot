using LeBot.Application.UseCases.AnswerInlineQuery;
using LeBot.Domain.Media;
using LeBot.Infrastructure.Telegram;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;

namespace LeBot.Infrastructure.Tests.Telegram;

public class TelegramInlineMessageEditorTests
{
    private static readonly Uri Url = new("https://www.tiktok.com/@a/video/1");

    private readonly ITelegramBotClient _bot = Substitute.For<ITelegramBotClient>();

    [Theory]
    [InlineData(true, "body")]
    [InlineData(false, null)]
    public async Task ShowPost_Video_SwapsInTheFile_CaptionedOnlyWithText(bool withText, string? caption)
    {
        await Sut().ShowPostAsync("im-1", new PreparedPost(MediaKind.Video, "file-1", "body"), withText, Url, null, CancellationToken.None);

        await _bot.Received(1).SendRequest(
            Arg.Is<EditInlineMessageMediaRequest>(r => r.InlineMessageId == "im-1" && IsVideo(r.Media, "file-1", caption) && KeepsOriginal(r.ReplyMarkup)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ShowPost_TextOnly_SwapsInTheText()
    {
        await Sut().ShowPostAsync("im-1", new PreparedPost(null, null, "just words"), withText: false, Url, null, CancellationToken.None);

        await _bot.Received(1).SendRequest(
            Arg.Is<EditInlineMessageTextRequest>(r => r.InlineMessageId == "im-1" && r.Text == "just words" && KeepsOriginal(r.ReplyMarkup)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ShowFailure_SaysSoAndKeepsTheLink()
    {
        await Sut().ShowFailureAsync("im-1", Url, null, CancellationToken.None);

        await _bot.Received(1).SendRequest(
            Arg.Is<EditInlineMessageTextRequest>(r => r.Text == $"{InlineResults.FailedTitle(BotLanguage.En)}\n{Url.AbsoluteUri}" && r.ReplyMarkup != null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ShowPost_PlaceholderAlreadyDeleted_DoesNotThrow()
    {
        _bot.SendRequest(Arg.Any<IRequest<bool>>(), Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new ApiRequestException("Bad Request: message to edit not found", 400));

        var show = () => Sut().ShowPostAsync("im-1", new PreparedPost(MediaKind.Video, "file-1", null), false, Url, null, CancellationToken.None);

        await show.Should().NotThrowAsync();
    }

    private TelegramInlineMessageEditor Sut() => new(_bot, NullLogger<TelegramInlineMessageEditor>.Instance);

    private static bool KeepsOriginal(global::Telegram.Bot.Types.ReplyMarkups.InlineKeyboardMarkup? markup) =>
        markup?.InlineKeyboard.Single().Single().Url == Url.AbsoluteUri;

    private static bool IsVideo(InputMedia media, string fileId, string? caption) =>
        media is InputMediaVideo video && video.Media.FileType == global::Telegram.Bot.Types.Enums.FileType.Id
        && ((InputFileId)video.Media).Id == fileId && video.Caption == caption;
}
