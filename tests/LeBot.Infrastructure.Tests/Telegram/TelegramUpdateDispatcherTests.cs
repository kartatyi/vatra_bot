using System.Collections.Concurrent;
using System.Threading.Channels;
using LeBot.Application.Metrics;
using LeBot.Application.Ports;
using LeBot.Application.Telemetry;
using LeBot.Application.UseCases.AnswerInlineQuery;
using LeBot.Application.UseCases.HandleIncomingMessage;
using LeBot.Infrastructure.Configuration;
using LeBot.Infrastructure.Maintenance;
using LeBot.Infrastructure.Telegram;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace LeBot.Infrastructure.Tests.Telegram;

/// <summary>
/// The dispatcher end to end over a faked Bot API: the poll loop, the per-chat lanes, and shutdown. Chat A's
/// <c>/stats</c> is the slow update — it parks on <see cref="_stats"/> until the test lets it go.
/// </summary>
public sealed class TelegramUpdateDispatcherTests : IAsyncLifetime
{
    private const long ChatA = -1001;
    private const long ChatB = -1002;
    private const string Pong = "🟢 OK";

    /// <summary>Only a guard against a hung run: every wait here is released by the test itself.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly ITelegramBotClient _bot = Substitute.For<ITelegramBotClient>();
    private readonly IRepostEventStore _events = Substitute.For<IRepostEventStore>();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero));

    private readonly ConcurrentQueue<Update[]> _batches = new();
    private readonly TaskCompletionSource<int> _caughtUp = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<RepostStatsSnapshot> _stats = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _statsAsked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<(long Chat, string Text)> _replies = new();
    private readonly Channel<bool> _replied = Channel.CreateUnbounded<bool>();

    private TelegramUpdateDispatcher? _sut;

    public TelegramUpdateDispatcherTests()
    {
        _bot.SendRequest(Arg.Any<GetMeRequest>(), Arg.Any<CancellationToken>())
            .Returns(new User { Id = 1, IsBot = true, FirstName = "Bot", Username = "test_bot" });
        _bot.SendRequest(Arg.Any<GetUpdatesRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => NextBatchAsync(call.Arg<GetUpdatesRequest>(), call.Arg<CancellationToken>()));
        _bot.SendRequest(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<SendMessageRequest>();
                _replies.Enqueue((request.ChatId.Identifier ?? 0, request.Text));
                _replied.Writer.TryWrite(true);
                return new Message { Id = 1 };
            });
        _events.GetStatsAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _statsAsked.TrySetResult();
                return _stats.Task.WaitAsync(call.Arg<CancellationToken>());
            });
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_sut is not null)
        {
            await _sut.StopAsync(CancellationToken.None);
            _sut.Dispose();
        }
    }

    public static TheoryData<Update, long> Lanes => new()
    {
        { new Update { Message = InChat(ChatA) }, ChatA },
        { new Update { CallbackQuery = new CallbackQuery { Id = "c", From = Person(7), Message = InChat(ChatA) } }, ChatA },
        { new Update { CallbackQuery = new CallbackQuery { Id = "c", From = Person(7), InlineMessageId = "im-1" } }, 7 },
        { new Update { MyChatMember = new ChatMemberUpdated { Chat = new Chat { Id = ChatA }, From = Person(7) } }, ChatA },
        { new Update { ChatMember = new ChatMemberUpdated { Chat = new Chat { Id = ChatA }, From = Person(7) } }, ChatA },
        { new Update { ChosenInlineResult = new ChosenInlineResult { ResultId = "r", From = Person(7), Query = "q" } }, 7 },
        { new Update { Poll = new Poll { Id = "p" } }, 0 },
    };

    [Theory]
    [MemberData(nameof(Lanes))]
    public void OrderingKeyOf_Update_IsItsChatOrElseItsSender(Update update, long lane)
    {
        TelegramUpdateDispatcher.OrderingKeyOf(update).Should().Be(lane);
    }

    [Fact]
    public async Task ExecuteAsync_SlowUpdateInOneChat_DoesNotHoldUpAnotherChat()
    {
        // Chat B's update arrives on a later poll, so it's only answered if the loop kept polling too.
        await StartAsync([Command(10, ChatA, "/stats")], [Command(11, ChatB, "/ping")]);

        await NextReplyAsync();

        _replies.Should().Equal((ChatB, Pong));
        _stats.Task.IsCompleted.Should().BeFalse("chat A's /stats is still waiting on its data");
    }

    [Fact]
    public async Task ExecuteAsync_TwoUpdatesOfOneChat_AreAnsweredInTheOrderSent()
    {
        await StartAsync([Command(10, ChatA, "/stats"), Command(11, ChatA, "/ping"), Command(12, ChatB, "/ping")]);
        await NextReplyAsync();
        var whileStatsWaits = _replies.ToList();

        _stats.SetResult(RepostStatsSnapshot.Empty);
        await NextReplyAsync();
        await NextReplyAsync();

        whileStatsWaits.Should().Equal(new[] { (ChatB, Pong) }, "chat A's /ping waits behind its /stats");
        _replies.Where(reply => reply.Chat == ChatA).Select(reply => reply.Text)
            .Should().HaveCount(2).And.EndWith(Pong, "chat A's /ping went in after its /stats");
    }

    [Fact]
    public async Task ExecuteAsync_UpdateThrows_LaterUpdatesOfThatChatAndOthersAreStillAnswered()
    {
        _events.GetStatsAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns<RepostStatsSnapshot>(_ => throw new InvalidOperationException("database is gone"));

        await StartAsync([Command(10, ChatA, "/stats"), Command(11, ChatA, "/ping"), Command(12, ChatB, "/ping")]);
        await NextReplyAsync();
        await NextReplyAsync();

        _replies.Should().BeEquivalentTo(new[] { (ChatA, Pong), (ChatB, Pong) });
    }

    [Fact]
    public async Task ExecuteAsync_UpdateStillBeingHandled_IsAcknowledgedOnTheNextPoll()
    {
        await StartAsync([Command(10, ChatA, "/stats")]);

        var nextOffset = await _caughtUp.Task.WaitAsync(Patience);

        nextOffset.Should().Be(11);
        _stats.Task.IsCompleted.Should().BeFalse("the update was acknowledged while it was still running");
    }

    [Fact]
    public async Task StopAsync_UpdateStillBeingHandled_CancelsItAndStops()
    {
        var sut = await StartAsync([Command(10, ChatA, "/stats")]);
        await _statsAsked.Task.WaitAsync(Patience);

        Func<Task> stop = () => sut.StopAsync(CancellationToken.None);

        await stop.Should().CompleteWithinAsync(Patience);
        _replies.Should().BeEmpty();
    }

    private async Task<TelegramUpdateDispatcher> StartAsync(params Update[][] batches)
    {
        foreach (var batch in batches)
        {
            _batches.Enqueue(batch);
        }

        _sut = Sut();
        await _sut.StartAsync(CancellationToken.None);
        return _sut;
    }

    private async Task NextReplyAsync() => await _replied.Reader.ReadAsync().AsTask().WaitAsync(Patience);

    /// <summary>The batches queued by the test, one per poll; after that, a long poll only shutdown ends.</summary>
    private async Task<Update[]> NextBatchAsync(GetUpdatesRequest request, CancellationToken cancellationToken)
    {
        if (_batches.TryDequeue(out var batch))
        {
            return batch;
        }

        _caughtUp.TrySetResult(request.Offset ?? 0);
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return [];
    }

    private TelegramUpdateDispatcher Sut()
    {
        var urls = Substitute.For<IUrlExtractor>();
        urls.Extract(Arg.Any<string>()).Returns([]);
        var cache = Substitute.For<IMediaCache>();
        var settings = Substitute.For<IChatSettingsStore>();
        var revealable = Substitute.For<IRevealableTextStore>();
        var metrics = new RepostMetrics(_time);
        var preparer = new InlinePostPreparer(
            cache, [], Substitute.For<IInlineMediaUploader>(), Substitute.For<IPreparedPostStore>(), _time, NullLogger<InlinePostPreparer>.Instance);

        return new TelegramUpdateDispatcher(
            _bot,
            new HandleIncomingMessageHandler(
                urls, [], cache, Substitute.For<ISeenLinkStore>(), settings, revealable, Substitute.For<ITelegramMessenger>(),
                metrics, Substitute.For<IRepostJournal>(), _time, NullLogger<HandleIncomingMessageHandler>.Instance),
            new AnswerInlineQueryHandler(urls, preparer, Substitute.For<IInlineAnswerer>(), _time, NullLogger<AnswerInlineQueryHandler>.Instance),
            new CompleteInlinePlaceholderHandler(urls, preparer, Substitute.For<IInlineMessageEditor>(), NullLogger<CompleteInlinePlaceholderHandler>.Instance),
            metrics,
            _events,
            Substitute.For<IStatsOptOutStore>(),
            new PersonStats(_events, Substitute.For<IPersonStatsStore>(), new StatsTimeZone(TimeZoneInfo.Utc)),
            new FeedbackRelay(_bot, Options.Create(new FeedbackOptions()), Options.Create(new UpdateOptions()), _time, NullLogger<FeedbackRelay>.Instance),
            settings,
            revealable,
            Substitute.For<IAppVersion>(),
            new BotHealthSignal(),
            Options.Create(new TelegramOptions { MaxConcurrentUpdates = 4 }),
            _time,
            NullLogger<TelegramUpdateDispatcher>.Instance);
    }

    private static Update Command(int updateId, long chatId, string text) => new()
    {
        Id = updateId,
        Message = InChat(chatId, text),
    };

    private static Message InChat(long chatId, string? text = null) => new()
    {
        Id = 100,
        Chat = new Chat { Id = chatId, Type = ChatType.Supergroup },
        From = Person(9),
        Text = text,
    };

    private static User Person(long id) => new() { Id = id, FirstName = "Petro" };
}
