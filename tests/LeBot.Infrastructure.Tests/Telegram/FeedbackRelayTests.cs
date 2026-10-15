using LeBot.Infrastructure.Configuration;
using LeBot.Infrastructure.Telegram;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace LeBot.Infrastructure.Tests.Telegram;

public class FeedbackRelayTests
{
    private const long Maintainer = 42;

    private readonly ITelegramBotClient _bot = Substitute.For<ITelegramBotClient>();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));

    public FeedbackRelayTests() =>
        _bot.SendRequest(Arg.Any<IRequest<Message>>(), Arg.Any<CancellationToken>()).Returns(new Message { Id = 1 });

    [Fact]
    public async Task Relay_SendsTheTextToTheMaintainer_AndThanksTheSender()
    {
        var reply = await Sut().RelayAsync(GroupMessage(), "  the bot missed a reel  ", CancellationToken.None);

        reply.Should().Be(FeedbackRelay.Thanks(BotLanguage.En));
        await _bot.Received(1).SendRequest(
            Arg.Is<SendMessageRequest>(r => r.ChatId == Maintainer && r.Text == "💬 Feedback from Petro K (@petro) in «Vatra»:\n\nthe bot missed a reel"),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task Relay_NoText_ExplainsUsageAndSendsNothing(string? text)
    {
        (await Sut().RelayAsync(GroupMessage(), text, CancellationToken.None)).Should().Be(FeedbackRelay.Usage(BotLanguage.En));

        await _bot.DidNotReceiveWithAnyArgs().SendRequest(Arg.Any<IRequest<Message>>(), default);
    }

    [Fact]
    public async Task Relay_NoMaintainerChat_SaysItIsNotSetUp()
    {
        (await Sut(feedbackChat: null, notifyChat: null).RelayAsync(GroupMessage(), "hi", CancellationToken.None))
            .Should().Be(FeedbackRelay.NotSetUp(BotLanguage.En));
    }

    [Fact]
    public async Task Relay_FallsBackToTheOperatorNotifyChat()
    {
        await Sut(feedbackChat: null, notifyChat: 7).RelayAsync(GroupMessage(), "hi", CancellationToken.None);

        await _bot.Received(1).SendRequest(Arg.Is<SendMessageRequest>(r => r.ChatId == 7), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Relay_SecondWithinTheCooldown_IsHeldBack_AfterItGoesThrough()
    {
        var sut = Sut();
        await sut.RelayAsync(GroupMessage(), "one", CancellationToken.None);

        var tooSoon = await sut.RelayAsync(GroupMessage(), "two", CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(61));
        var later = await sut.RelayAsync(GroupMessage(), "three", CancellationToken.None);

        tooSoon.Should().Be(FeedbackRelay.TooSoon(BotLanguage.En));
        later.Should().Be(FeedbackRelay.Thanks(BotLanguage.En));
    }

    [Fact]
    public async Task Relay_TelegramRefuses_SaysSoAndDoesNotStartTheCooldown()
    {
        _bot.SendRequest(Arg.Any<IRequest<Message>>(), Arg.Any<CancellationToken>())
            .Returns<Message>(_ => throw new ApiRequestException("Forbidden: bot was blocked by the user", 403));

        (await Sut().RelayAsync(GroupMessage(), "hi", CancellationToken.None)).Should().Be(FeedbackRelay.Failed(BotLanguage.En));
    }

    [Fact]
    public void Format_PrivateChatAndNoUsername_ReadsNaturally()
    {
        var message = new Message
        {
            Chat = new Chat { Id = 5, Type = ChatType.Private },
            From = new User { Id = 5, FirstName = "Olena" },
        };

        FeedbackRelay.Format(message, "nice bot").Should().Be("💬 Feedback from Olena in a private chat:\n\nnice bot");
    }

    private FeedbackRelay Sut(long? feedbackChat = Maintainer, long? notifyChat = null) => new(
        _bot,
        Options.Create(new FeedbackOptions { ChatId = feedbackChat }),
        Options.Create(new UpdateOptions { NotifyChatId = notifyChat }),
        _time,
        NullLogger<FeedbackRelay>.Instance);

    private static Message GroupMessage() => new()
    {
        Chat = new Chat { Id = -100, Type = ChatType.Supergroup, Title = "Vatra" },
        From = new User { Id = 9, FirstName = "Petro", LastName = "K", Username = "petro" },
    };
}
