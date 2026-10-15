using LeBot.Infrastructure.Telegram;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;

namespace LeBot.Infrastructure.Tests.Telegram;

public class TelegramWorkingReactionTests
{
    private readonly ITelegramBotClient _bot = Substitute.For<ITelegramBotClient>();

    [Fact]
    public async Task PlaceAsync_ThenDispose_SetsTheReactionThenClearsIt()
    {
        await using (await TelegramWorkingReaction.PlaceAsync(_bot, 1L, 2, NullLogger.Instance, CancellationToken.None))
        {
            await _bot.Received(1).SendRequest(
                Arg.Is<SetMessageReactionRequest>(r => IsReaction(r, TelegramWorkingReaction.Emoji)),
                Arg.Any<CancellationToken>());
        }

        await _bot.Received(1).SendRequest(
            Arg.Is<SetMessageReactionRequest>(r => IsReaction(r, null)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PlaceAsync_ReactionRefused_NeitherThrowsNorClearsLater()
    {
        _bot.SendRequest(Arg.Any<IRequest<bool>>(), Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new ApiRequestException("Bad Request: REACTION_INVALID", 400));

        var place = async () =>
        {
            await using var reaction = await TelegramWorkingReaction.PlaceAsync(_bot, 1L, 2, NullLogger.Instance, CancellationToken.None);
        };

        await place.Should().NotThrowAsync();
        await _bot.Received(1).SendRequest(Arg.Any<IRequest<bool>>(), Arg.Any<CancellationToken>());
    }

    // null means "the request clears every reaction".
    private static bool IsReaction(SetMessageReactionRequest request, string? emoji) =>
        emoji is null
            ? request.Reaction is null || !request.Reaction.Any()
            : request.Reaction?.SingleOrDefault() is ReactionTypeEmoji single && single.Emoji == emoji;
}
