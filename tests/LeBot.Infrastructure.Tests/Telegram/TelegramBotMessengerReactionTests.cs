using LeBot.Application.Ports;
using LeBot.Infrastructure.Telegram;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;

namespace LeBot.Infrastructure.Tests.Telegram;

public class TelegramBotMessengerReactionTests
{
    private readonly ITelegramBotClient _bot = Substitute.For<ITelegramBotClient>();

    [Theory]
    [InlineData(LinkReaction.Working, "👀")]
    [InlineData(LinkReaction.Repeat, "🥱")]
    public async Task SetReactionAsync_SetsTheReactionsEmoji(LinkReaction reaction, string emoji)
    {
        await CreateSut().SetReactionAsync(1L, 2, reaction, CancellationToken.None);

        await _bot.Received(1).SendRequest(
            Arg.Is<SetMessageReactionRequest>(r => HasOnly(r, emoji)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetReactionAsync_Null_ClearsTheReaction()
    {
        await CreateSut().SetReactionAsync(1L, 2, null, CancellationToken.None);

        await _bot.Received(1).SendRequest(
            Arg.Is<SetMessageReactionRequest>(r => IsEmpty(r)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetReactionAsync_ChatRefusesTheReaction_DoesNotThrow()
    {
        _bot.SendRequest(Arg.Any<IRequest<bool>>(), Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new ApiRequestException("Bad Request: REACTION_INVALID", 400));

        var react = () => CreateSut().SetReactionAsync(1L, 2, LinkReaction.Working, CancellationToken.None);

        await react.Should().NotThrowAsync();
    }

    private TelegramBotMessenger CreateSut() => new(_bot, NullLogger<TelegramBotMessenger>.Instance);

    private static bool HasOnly(SetMessageReactionRequest request, string emoji) =>
        request.Reaction?.SingleOrDefault() is ReactionTypeEmoji single && single.Emoji == emoji;

    private static bool IsEmpty(SetMessageReactionRequest request) =>
        request.Reaction is null || !request.Reaction.Any();
}
