using LeBot.Application.Caching;
using LeBot.Application.Metrics;
using LeBot.Application.Ports;
using LeBot.Application.Telemetry;
using LeBot.Application.UseCases.HandleIncomingMessage;
using LeBot.Domain.Common;
using LeBot.Domain.Media;
using LeBot.Domain.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LeBot.Application.Tests.UseCases;

public class HandleIncomingMessageHandlerTests
{
    private readonly IUrlExtractor _urlExtractor = Substitute.For<IUrlExtractor>();
    private readonly IPlatformExtractor _extractor = Substitute.For<IPlatformExtractor>();
    private readonly IMediaCache _cache = Substitute.For<IMediaCache>();
    private readonly ISeenLinkStore _seenLinks = Substitute.For<ISeenLinkStore>();
    private readonly ITelegramMessenger _messenger = Substitute.For<ITelegramMessenger>();
    private readonly IChatSettingsStore _settings = Substitute.For<IChatSettingsStore>();
    private readonly IRevealableTextStore _revealable = Substitute.For<IRevealableTextStore>();
    private readonly RepostMetrics _metrics = new(TimeProvider.System);
    private readonly IRepostJournal _journal = Substitute.For<IRepostJournal>();
    private readonly TimeProvider _time = TimeProvider.System;
    private readonly ILogger<HandleIncomingMessageHandler> _logger = NullLogger<HandleIncomingMessageHandler>.Instance;

    // Full/Full shapes nothing, so the tests that aren't about captions can assert on the payload
    // the extractor returned; the caption tests below set their own settings.
    public HandleIncomingMessageHandlerTests()
    {
        _settings.GetAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new ChatSettings(CaptionMode.Full, CaptionMode.Full));

        // A store that sends what it was given — the disk store's behaviour.
        _cache.SaveAsync(Arg.Any<MediaPayload>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<MediaPayload>());
    }

    private HandleIncomingMessageHandler CreateSut() =>
        new(_urlExtractor, [_extractor], _cache, _seenLinks, _settings, _revealable, _messenger, _metrics, _journal, _time, _logger);

    private static IncomingMessage Message(string text = "hi") =>
        new(ChatId: 123L, MessageId: 7, Text: text, SenderUsername: "user", SenderId: 555L);

    [Fact]
    public async Task HandleAsync_NoUrls_DoesNothing()
    {
        _urlExtractor.Extract(Arg.Any<string>()).Returns([]);

        await CreateSut().HandleAsync(Message(), CancellationToken.None);

        await _extractor.DidNotReceiveWithAnyArgs().ExtractAsync(default!, default);
        await _messenger.DidNotReceiveWithAnyArgs().ReplyWithMediaAsync(default, default, default!, default);
        await _messenger.DidNotReceiveWithAnyArgs().ReplyWithTextAsync(default, default, default!, default);
        _messenger.DidNotReceiveWithAnyArgs().IndicateBusy(default, default);
    }

    [Fact]
    public async Task HandleAsync_SupportedUrl_RaisesBusyIndicator()
    {
        var url = new Uri("https://example.com/x");
        var item = new MediaItem("/tmp/x.mp4", MediaKind.Video, null, null, null);
        var payload = new MediaPayload(url, null, null, [item]);

        _urlExtractor.Extract(Arg.Any<string>()).Returns([url]);
        _extractor.CanHandle(url).Returns(true);
        _extractor.ExtractAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result<MediaPayload, ExtractionError>.Success(payload));

        await CreateSut().HandleAsync(Message(), CancellationToken.None);

        _messenger.Received(1).IndicateBusy(123L, BusyKind.UploadingVideo);
    }

    [Fact]
    public async Task HandleAsync_CacheMiss_ReactsWorkingWhileItWorks_ThenClears()
    {
        var url = new Uri("https://example.com/x");
        var payload = VideoPayload(url);
        ArrangeExtraction(url, payload);

        await CreateSut().HandleAsync(Message(), CancellationToken.None);

        Received.InOrder(() =>
        {
            _messenger.SetReactionAsync(123L, 7, LinkReaction.Working, Arg.Any<CancellationToken>());
            _messenger.ReplyWithMediaAsync(123L, 7, payload, Arg.Any<CancellationToken>());
            _messenger.SetReactionAsync(123L, 7, null, Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task HandleAsync_StatsOff_RecordsNoSenderAnywhere()
    {
        var url = new Uri("https://example.com/x");
        var payload = VideoPayload(url);
        ArrangeExtraction(url, payload);
        _settings.GetAsync(123L, Arg.Any<CancellationToken>())
            .Returns(new ChatSettings(CaptionMode.Full, CaptionMode.Full, KeepStats: false));

        await CreateSut().HandleAsync(Message(), CancellationToken.None);

        await _seenLinks.Received(1).MarkSeenAsync(123L, url, null, Arg.Any<CancellationToken>());
        await _journal.Received(1).RecordMediaRepostAsync(
            url, Arg.Any<string>(), 1, Arg.Any<long?>(), Arg.Any<TimeSpan>(), 123L, Arg.Is<long?>(sender => sender == null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_RecordsWhoPostedTheLink()
    {
        var url = new Uri("https://example.com/x");
        ArrangeExtraction(url, VideoPayload(url));

        await CreateSut().HandleAsync(Message(), CancellationToken.None);

        await _seenLinks.Received(1).MarkSeenAsync(123L, url, 555L, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_RepeatLink_SettlesOnTheRepeatReaction()
    {
        var url = new Uri("https://example.com/x");
        ArrangeExtraction(url, VideoPayload(url));
        _seenLinks.MarkSeenAsync(123L, url, Arg.Any<long?>(), Arg.Any<CancellationToken>()).Returns(true);

        await CreateSut().HandleAsync(Message(), CancellationToken.None);

        Received.InOrder(() =>
        {
            _messenger.SetReactionAsync(123L, 7, LinkReaction.Working, Arg.Any<CancellationToken>());
            _messenger.SetReactionAsync(123L, 7, LinkReaction.Repeat, Arg.Any<CancellationToken>());
        });
        await _messenger.DidNotReceive().SetReactionAsync(123L, 7, null, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(true, LinkReaction.Failed)]
    [InlineData(false, null)]
    public async Task HandleAsync_ExtractorTriedAndFailed_ShrugsOnlyWhereTheChatOptedIn(bool shrug, LinkReaction? expected)
    {
        var url = new Uri("https://example.com/x");
        _settings.GetAsync(123L, Arg.Any<CancellationToken>())
            .Returns(new ChatSettings(CaptionMode.Full, CaptionMode.Full, ShrugOnFailure: shrug));
        _urlExtractor.Extract(Arg.Any<string>()).Returns([url]);
        _extractor.CanHandle(url).Returns(true);
        _extractor.ExtractAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result<MediaPayload, ExtractionError>.Failure(new ExtractionError.ContentUnavailable(url, "gone")));

        await CreateSut().HandleAsync(Message(), CancellationToken.None);

        await _messenger.Received(1).SetReactionAsync(123L, 7, expected, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_NoExtractorClaimsTheLink_NeverShrugs()
    {
        var url = new Uri("https://example.com/news");
        _settings.GetAsync(123L, Arg.Any<CancellationToken>())
            .Returns(new ChatSettings(CaptionMode.Full, CaptionMode.Full, ShrugOnFailure: true));
        _urlExtractor.Extract(Arg.Any<string>()).Returns([url]);
        _extractor.CanHandle(url).Returns(true);
        _extractor.ExtractAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result<MediaPayload, ExtractionError>.Failure(new ExtractionError.UnsupportedPlatform(url)));

        await CreateSut().HandleAsync(Message(), CancellationToken.None);

        await _messenger.DidNotReceive().SetReactionAsync(123L, 7, LinkReaction.Failed, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ExtractorThrows_StillClearsTheWorkingReaction()
    {
        var url = new Uri("https://example.com/x");
        _urlExtractor.Extract(Arg.Any<string>()).Returns([url]);
        _extractor.CanHandle(url).Returns(true);
        _extractor.ExtractAsync(url, Arg.Any<CancellationToken>())
            .Returns<Result<MediaPayload, ExtractionError>>(_ => throw new InvalidOperationException("boom"));

        var handle = () => CreateSut().HandleAsync(Message(), CancellationToken.None);

        await handle.Should().ThrowAsync<InvalidOperationException>();
        await _messenger.Received(1).SetReactionAsync(123L, 7, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_CacheHit_ReactsOnlyWhenItIsARepeat()
    {
        var url = new Uri("https://tiktok.com/@user/video/1");
        var item = new MediaItem("/cache/ab/00.mp4", MediaKind.Video, "video/mp4", 100, 5);
        var stored = new MediaPayload(url, null, "user", [item], "body", RetainFiles: true);
        _urlExtractor.Extract(Arg.Any<string>()).Returns([url]);
        _extractor.CanHandle(url).Returns(true);
        _cache.TryGetAsync(url, Arg.Any<CancellationToken>()).Returns(new CachedRepost(stored, "YtDlpPlatformExtractor"));
        _seenLinks.MarkSeenAsync(123L, url, Arg.Any<long?>(), Arg.Any<CancellationToken>()).Returns(true);

        await CreateSut().HandleAsync(Message(), CancellationToken.None);

        await _messenger.Received(1).SetReactionAsync(123L, 7, LinkReaction.Repeat, Arg.Any<CancellationToken>());
        await _messenger.DidNotReceive().SetReactionAsync(123L, 7, LinkReaction.Working, Arg.Any<CancellationToken>());
    }

    private static MediaPayload VideoPayload(Uri url) =>
        new(url, null, null, [new MediaItem("/tmp/x.mp4", MediaKind.Video, null, null, null)]);

    private void ArrangeExtraction(Uri url, MediaPayload payload)
    {
        _urlExtractor.Extract(Arg.Any<string>()).Returns([url]);
        _extractor.CanHandle(url).Returns(true);
        _extractor.ExtractAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result<MediaPayload, ExtractionError>.Success(payload));
    }

    [Fact]
    public async Task HandleAsync_UnsupportedUrl_DoesNotRaiseBusyIndicator()
    {
        var url = new Uri("https://unsupported.example.com/x");
        _urlExtractor.Extract(Arg.Any<string>()).Returns([url]);
        _extractor.CanHandle(url).Returns(false);

        await CreateSut().HandleAsync(Message(), CancellationToken.None);

        _messenger.DidNotReceiveWithAnyArgs().IndicateBusy(default, default);
    }

    [Fact]
    public async Task HandleAsync_NoExtractorForUrl_DoesNotCallMessenger()
    {
        var url = new Uri("https://unsupported.example.com/x");
        _urlExtractor.Extract(Arg.Any<string>()).Returns([url]);
        _extractor.CanHandle(url).Returns(false);

        await CreateSut().HandleAsync(Message(), CancellationToken.None);

        await _extractor.DidNotReceiveWithAnyArgs().ExtractAsync(default!, default);
        await _messenger.DidNotReceiveWithAnyArgs().ReplyWithMediaAsync(default, default, default!, default);
        await _messenger.DidNotReceiveWithAnyArgs().ReplyWithTextAsync(default, default, default!, default);
    }

    [Fact]
    public async Task HandleAsync_ExtractorSucceeds_RepliesWithMedia()
    {
        var url = new Uri("https://example.com/x");
        var item = new MediaItem("/tmp/x.mp4", MediaKind.Video, "video/mp4", 100, 5);
        var payload = new MediaPayload(url, "title", "author", [item]);

        _urlExtractor.Extract(Arg.Any<string>()).Returns([url]);
        _extractor.CanHandle(url).Returns(true);
        _extractor.ExtractAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result<MediaPayload, ExtractionError>.Success(payload));

        await CreateSut().HandleAsync(Message(), CancellationToken.None);

        await _messenger.Received(1).ReplyWithMediaAsync(
            123L, 7, payload, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_VideoUnderDefaultSettings_RepostsWithoutCaption_CachesFullText()
    {
        var url = new Uri("https://www.tiktok.com/@user/video/1");
        var item = new MediaItem("/tmp/x.mp4", MediaKind.Video, "video/mp4", 100, 5);
        var payload = new MediaPayload(url, "Video by user", "user", [item], "promo #fyp");
        _settings.GetAsync(123L, Arg.Any<CancellationToken>()).Returns(ChatSettings.Default);

        _urlExtractor.Extract(Arg.Any<string>()).Returns([url]);
        _extractor.CanHandle(url).Returns(true);
        _extractor.ExtractAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result<MediaPayload, ExtractionError>.Success(payload));

        await CreateSut().HandleAsync(Message(), CancellationToken.None);

        await _messenger.Received(1).ReplyWithMediaAsync(
            123L, 7,
            payload with { Title = null, Description = null, Author = null, RevealableText = payload.Body },
            Arg.Any<CancellationToken>());
        await _cache.Received(1).SaveAsync(payload, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_HiddenText_IsKeptForTheRepostsButton()
    {
        var url = new Uri("https://www.tiktok.com/@user/video/1");
        var item = new MediaItem("/tmp/x.mp4", MediaKind.Video, "video/mp4", 100, 5);
        var payload = new MediaPayload(url, null, "user", [item], "the recipe");
        _settings.GetAsync(123L, Arg.Any<CancellationToken>()).Returns(ChatSettings.Default);
        _messenger.ReplyWithMediaAsync(123L, 7, Arg.Any<MediaPayload>(), Arg.Any<CancellationToken>()).Returns(42);

        _urlExtractor.Extract(Arg.Any<string>()).Returns([url]);
        _extractor.CanHandle(url).Returns(true);
        _extractor.ExtractAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result<MediaPayload, ExtractionError>.Success(payload));

        await CreateSut().HandleAsync(Message(), CancellationToken.None);

        await _revealable.Received(1).SaveAsync(123L, 42, "the recipe\n\n— user", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_HiddenTextOnAnAlbum_KeepsNothing()
    {
        var url = new Uri("https://www.tiktok.com/@user/video/1");
        var items = new[]
        {
            new MediaItem("/tmp/1.mp4", MediaKind.Video, "video/mp4", 100, 5),
            new MediaItem("/tmp/2.mp4", MediaKind.Video, "video/mp4", 100, 5),
        };
        var payload = new MediaPayload(url, null, "user", items, "the recipe");
        _settings.GetAsync(123L, Arg.Any<CancellationToken>()).Returns(ChatSettings.Default);
        _messenger.ReplyWithMediaAsync(123L, 7, Arg.Any<MediaPayload>(), Arg.Any<CancellationToken>()).Returns((int?)null);

        _urlExtractor.Extract(Arg.Any<string>()).Returns([url]);
        _extractor.CanHandle(url).Returns(true);
        _extractor.ExtractAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result<MediaPayload, ExtractionError>.Success(payload));

        await CreateSut().HandleAsync(Message(), CancellationToken.None);

        await _revealable.DidNotReceiveWithAnyArgs().SaveAsync(default, default, default!, default);
    }

    [Fact]
    public async Task HandleAsync_CacheHit_ShapesStoredPayloadForThisChat()
    {
        var url = new Uri("https://www.tiktok.com/@user/video/1");
        var item = new MediaItem("/cache/ab/00.mp4", MediaKind.Video, "video/mp4", 100, 5);
        var stored = new MediaPayload(url, null, "user", [item], "Nice clip #fyp", RetainFiles: true);
        _settings.GetAsync(123L, Arg.Any<CancellationToken>())
            .Returns(new ChatSettings(CaptionMode.Brief, CaptionMode.Full));

        _urlExtractor.Extract(Arg.Any<string>()).Returns([url]);
        _extractor.CanHandle(url).Returns(true);
        _cache.TryGetAsync(url, Arg.Any<CancellationToken>())
            .Returns(new CachedRepost(stored, "YtDlpPlatformExtractor"));

        await CreateSut().HandleAsync(Message(), CancellationToken.None);

        await _messenger.Received(1).ReplyWithMediaAsync(
            123L, 7, stored with { Description = "Nice clip", RevealableText = stored.Body }, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_FreshMedia_SendsThePostTheStoreHandsBack()
    {
        var url = new Uri("https://example.com/x");
        var downloaded = VideoPayload(url);
        var stored = downloaded with
        {
            Items = [new MediaItem(string.Empty, MediaKind.Video, null, null, null) { TelegramFileId = "vid-1" }],
            RetainFiles = true,
        };
        ArrangeExtraction(url, downloaded);
        _cache.SaveAsync(downloaded, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(stored);

        await CreateSut().HandleAsync(Message(), CancellationToken.None);

        // Uploaded once, to the storage chat; the chat gets it by file id.
        await _messenger.Received(1).ReplyWithMediaAsync(123L, 7, stored, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_EmptyPayload_StaysSilent()
    {
        var url = new Uri("https://example.com/x");
        var payload = new MediaPayload(url, null, null, []);

        _urlExtractor.Extract(Arg.Any<string>()).Returns([url]);
        _extractor.CanHandle(url).Returns(true);
        _extractor.ExtractAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result<MediaPayload, ExtractionError>.Success(payload));

        await CreateSut().HandleAsync(Message(), CancellationToken.None);

        // No media and no replyable text — the bot says nothing rather than posting a
        // "couldn't extract" notice.
        await _messenger.DidNotReceiveWithAnyArgs().ReplyWithMediaAsync(default, default, default!, default);
        await _messenger.DidNotReceiveWithAnyArgs().ReplyWithTextAsync(default, default, default!, default);
    }

    [Fact]
    public async Task HandleAsync_NoMediaButDescription_SendsTextReply()
    {
        var url = new Uri("https://example.com/x");
        var payload = new MediaPayload(url, null, "saab", [], Description: "Gripen announcement body text");

        _urlExtractor.Extract(Arg.Any<string>()).Returns([url]);
        _extractor.CanHandle(url).Returns(true);
        _extractor.ExtractAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result<MediaPayload, ExtractionError>.Success(payload));

        await CreateSut().HandleAsync(Message(), CancellationToken.None);

        await _messenger.Received(1).ReplyWithTextAsync(
            123L, 7, payload, Arg.Any<CancellationToken>());
        await _messenger.DidNotReceiveWithAnyArgs().ReplyWithMediaAsync(default, default, default!, default);
    }

    [Fact]
    public async Task HandleAsync_NoMediaButTitleOnly_SendsTextReply()
    {
        var url = new Uri("https://example.com/x");
        var payload = new MediaPayload(url, Title: "Just a title", Author: null, Items: []);

        _urlExtractor.Extract(Arg.Any<string>()).Returns([url]);
        _extractor.CanHandle(url).Returns(true);
        _extractor.ExtractAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result<MediaPayload, ExtractionError>.Success(payload));

        await CreateSut().HandleAsync(Message(), CancellationToken.None);

        await _messenger.Received(1).ReplyWithTextAsync(
            123L, 7, payload, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ExtractorReturnsUnsupportedPlatform_StaysSilent()
    {
        var url = new Uri("https://github.com/user/repo");
        _urlExtractor.Extract(Arg.Any<string>()).Returns([url]);
        _extractor.CanHandle(url).Returns(true);
        _extractor.ExtractAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result<MediaPayload, ExtractionError>.Failure(new ExtractionError.UnsupportedPlatform(url)));

        await CreateSut().HandleAsync(Message(), CancellationToken.None);

        // UnsupportedPlatform is the "this isn't ours" signal — never produce a reply for it,
        // even when CanHandle was optimistically true. Otherwise every random github / news URL
        // gets a "Couldn't extract media" message and the chat fills with noise.
        await _messenger.DidNotReceiveWithAnyArgs().ReplyWithMediaAsync(default, default, default!, default);
        await _messenger.DidNotReceiveWithAnyArgs().ReplyWithTextAsync(default, default, default!, default);
    }

    [Fact]
    public async Task HandleAsync_ExtractorFails_StaysSilent()
    {
        var url = new Uri("https://example.com/x");
        var error = new ExtractionError.ContentUnavailable(url, "gone");

        _urlExtractor.Extract(Arg.Any<string>()).Returns([url]);
        _extractor.CanHandle(url).Returns(true);
        _extractor.ExtractAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result<MediaPayload, ExtractionError>.Failure(error));

        await CreateSut().HandleAsync(Message(), CancellationToken.None);

        // A hard extraction failure is counted but no longer acknowledged in-chat.
        await _messenger.DidNotReceiveWithAnyArgs().ReplyWithMediaAsync(default, default, default!, default);
        await _messenger.DidNotReceiveWithAnyArgs().ReplyWithTextAsync(default, default, default!, default);
        _metrics.Failures.Should().Be(1);
    }

    [Fact]
    public async Task HandleAsync_FirstExtractorFails_SecondExtractorSucceeds_SendsItsMedia()
    {
        var url = new Uri("https://instagram.com/p/abc/");
        var item = new MediaItem("/tmp/x.jpg", MediaKind.Photo, "image/jpeg", 100, null);
        var goodPayload = new MediaPayload(url, null, "saab", [item], "post body");

        var first = Substitute.For<IPlatformExtractor>();
        first.CanHandle(url).Returns(true);
        first.ExtractAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result<MediaPayload, ExtractionError>.Failure(new ExtractionError.NetworkFailure(url, "boom")));

        var second = Substitute.For<IPlatformExtractor>();
        second.CanHandle(url).Returns(true);
        second.ExtractAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result<MediaPayload, ExtractionError>.Success(goodPayload));

        _urlExtractor.Extract(Arg.Any<string>()).Returns([url]);

        var sut = new HandleIncomingMessageHandler(_urlExtractor, [first, second], _cache, _seenLinks, _settings, _revealable, _messenger, _metrics, _journal, _time, _logger);
        await sut.HandleAsync(Message(), CancellationToken.None);

        await _messenger.Received(1).ReplyWithMediaAsync(123L, 7, goodPayload, Arg.Any<CancellationToken>());
        await _messenger.DidNotReceiveWithAnyArgs().ReplyWithTextAsync(default, default, default!, default);
    }

    [Fact]
    public async Task HandleAsync_FirstExtractorEmpty_SecondExtractorText_PrefersFirstText()
    {
        var url = new Uri("https://instagram.com/p/abc/");
        var firstPayload = new MediaPayload(url, null, "first", [], "from first extractor");
        var secondPayload = new MediaPayload(url, null, "second", [], "from second extractor");

        var first = Substitute.For<IPlatformExtractor>();
        first.CanHandle(url).Returns(true);
        first.ExtractAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result<MediaPayload, ExtractionError>.Success(firstPayload));

        var second = Substitute.For<IPlatformExtractor>();
        second.CanHandle(url).Returns(true);
        second.ExtractAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result<MediaPayload, ExtractionError>.Success(secondPayload));

        _urlExtractor.Extract(Arg.Any<string>()).Returns([url]);

        var sut = new HandleIncomingMessageHandler(_urlExtractor, [first, second], _cache, _seenLinks, _settings, _revealable, _messenger, _metrics, _journal, _time, _logger);
        await sut.HandleAsync(Message(), CancellationToken.None);

        // Both ran (no media wins, so chain keeps going); first text wins as primary fallback.
        await _messenger.Received(1).ReplyWithTextAsync(123L, 7, firstPayload, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_MultipleUrls_ProcessesEach()
    {
        var u1 = new Uri("https://example.com/1");
        var u2 = new Uri("https://example.com/2");
        var item = new MediaItem("/tmp/x.mp4", MediaKind.Video, null, null, null);
        var p1 = new MediaPayload(u1, null, null, [item]);
        var p2 = new MediaPayload(u2, null, null, [item]);

        _urlExtractor.Extract(Arg.Any<string>()).Returns([u1, u2]);
        _extractor.CanHandle(Arg.Any<Uri>()).Returns(true);
        _extractor.ExtractAsync(u1, Arg.Any<CancellationToken>())
            .Returns(Result<MediaPayload, ExtractionError>.Success(p1));
        _extractor.ExtractAsync(u2, Arg.Any<CancellationToken>())
            .Returns(Result<MediaPayload, ExtractionError>.Success(p2));

        await CreateSut().HandleAsync(Message(), CancellationToken.None);

        await _messenger.Received(2).ReplyWithMediaAsync(
            Arg.Any<long>(), Arg.Any<int>(), Arg.Any<MediaPayload>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_UrlAlreadyCached_RepostsStoredMediaWithoutExtracting()
    {
        var url = new Uri("https://tiktok.com/@user/video/1");
        var item = new MediaItem("/cache/ab/00.mp4", MediaKind.Video, "video/mp4", 100, 5);
        var stored = new MediaPayload(url, null, "user", [item], "body", RetainFiles: true);

        _urlExtractor.Extract(Arg.Any<string>()).Returns([url]);
        _extractor.CanHandle(url).Returns(true);
        _cache.TryGetAsync(url, Arg.Any<CancellationToken>())
            .Returns(new CachedRepost(stored, "YtDlpPlatformExtractor"));

        await CreateSut().HandleAsync(Message(), CancellationToken.None);

        // The whole point of the cache: the platform is never contacted for a link we've seen.
        await _extractor.DidNotReceiveWithAnyArgs().ExtractAsync(default!, default);
        await _messenger.Received(1).ReplyWithMediaAsync(123L, 7, stored, Arg.Any<CancellationToken>());
        _metrics.CacheHits.Should().Be(1);
        _metrics.MediaReposts.Should().Be(1);
        _metrics.ByExtractor.Should().ContainKey("YtDlpPlatformExtractor");
    }

    [Fact]
    public async Task HandleAsync_CachedTextOnlyPayload_SendsTextReply()
    {
        var url = new Uri("https://threads.com/@user/post/1");
        var stored = new MediaPayload(url, null, "user", [], "post body", RetainFiles: true);

        _urlExtractor.Extract(Arg.Any<string>()).Returns([url]);
        _extractor.CanHandle(url).Returns(true);
        _cache.TryGetAsync(url, Arg.Any<CancellationToken>())
            .Returns(new CachedRepost(stored, "ThreadsEmbedExtractor"));

        await CreateSut().HandleAsync(Message(), CancellationToken.None);

        await _messenger.Received(1).ReplyWithTextAsync(123L, 7, stored, Arg.Any<CancellationToken>());
        await _extractor.DidNotReceiveWithAnyArgs().ExtractAsync(default!, default);
        _metrics.CacheHits.Should().Be(1);
    }

    [Fact]
    public async Task HandleAsync_CachedMediaServed_JournalsItUnderTheCacheName()
    {
        var url = new Uri("https://tiktok.com/@user/video/1");
        var item = new MediaItem("/cache/ab/00.mp4", MediaKind.Video, "video/mp4", 100, 5);
        var stored = new MediaPayload(url, null, "user", [item], "body", RetainFiles: true);

        _urlExtractor.Extract(Arg.Any<string>()).Returns([url]);
        _extractor.CanHandle(url).Returns(true);
        _cache.TryGetAsync(url, Arg.Any<CancellationToken>())
            .Returns(new CachedRepost(stored, "YtDlpPlatformExtractor"));

        await CreateSut().HandleAsync(Message(), CancellationToken.None);

        // A served link is a repost the dashboard must count, or the failure rate reads high
        // for no reason. It is filed under MediaCache, not the extractor that never ran.
        await _journal.Received(1).RecordMediaRepostAsync(
            url, "MediaCache", 1, 100, Arg.Any<TimeSpan>(), 123L, 555L, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_CachedTextServed_JournalsItUnderTheCacheName()
    {
        var url = new Uri("https://threads.com/@user/post/1");
        var stored = new MediaPayload(url, null, "user", [], "post body", RetainFiles: true);

        _urlExtractor.Extract(Arg.Any<string>()).Returns([url]);
        _extractor.CanHandle(url).Returns(true);
        _cache.TryGetAsync(url, Arg.Any<CancellationToken>())
            .Returns(new CachedRepost(stored, "ThreadsEmbedExtractor"));

        await CreateSut().HandleAsync(Message(), CancellationToken.None);

        await _journal.Received(1).RecordTextFallbackAsync(
            url, "MediaCache", Arg.Any<TimeSpan>(), 123L, 555L, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_CachedPayloadHasNothingToSay_FallsBackToExtraction()
    {
        var url = new Uri("https://example.com/x");
        var hollow = new MediaPayload(url, null, null, [], RetainFiles: true);
        var item = new MediaItem("/tmp/x.mp4", MediaKind.Video, null, null, null);
        var fresh = new MediaPayload(url, "title", null, [item]);

        _urlExtractor.Extract(Arg.Any<string>()).Returns([url]);
        _extractor.CanHandle(url).Returns(true);
        _cache.TryGetAsync(url, Arg.Any<CancellationToken>())
            .Returns(new CachedRepost(hollow, "YtDlpPlatformExtractor"));
        _extractor.ExtractAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result<MediaPayload, ExtractionError>.Success(fresh));

        await CreateSut().HandleAsync(Message(), CancellationToken.None);

        await _messenger.Received(1).ReplyWithMediaAsync(123L, 7, fresh, Arg.Any<CancellationToken>());
        _metrics.CacheHits.Should().Be(0);
    }

    [Fact]
    public async Task HandleAsync_ExtractorSucceeds_CachesPayloadBeforeSending()
    {
        var url = new Uri("https://example.com/x");
        var item = new MediaItem("/tmp/x.mp4", MediaKind.Video, "video/mp4", 100, 5);
        var payload = new MediaPayload(url, "title", "author", [item]);

        _urlExtractor.Extract(Arg.Any<string>()).Returns([url]);
        _extractor.CanHandle(url).Returns(true);
        _extractor.ExtractAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result<MediaPayload, ExtractionError>.Success(payload));

        await CreateSut().HandleAsync(Message(), CancellationToken.None);

        // Ordering matters: the messenger deletes the files once the upload finishes, so a save
        // that ran afterwards would have nothing left to copy.
        Received.InOrder(() =>
        {
            _cache.SaveAsync(payload, _extractor.GetType().Name, Arg.Any<CancellationToken>());
            _messenger.ReplyWithMediaAsync(123L, 7, payload, Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task HandleAsync_TextFallback_IsCachedToo()
    {
        var url = new Uri("https://example.com/x");
        var payload = new MediaPayload(url, null, "author", [], Description: "body text");

        _urlExtractor.Extract(Arg.Any<string>()).Returns([url]);
        _extractor.CanHandle(url).Returns(true);
        _extractor.ExtractAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result<MediaPayload, ExtractionError>.Success(payload));

        await CreateSut().HandleAsync(Message(), CancellationToken.None);

        await _cache.Received(1).SaveAsync(payload, _extractor.GetType().Name, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_NoExtractorForUrl_DoesNotTouchCache()
    {
        var url = new Uri("https://unsupported.example.com/x");
        _urlExtractor.Extract(Arg.Any<string>()).Returns([url]);
        _extractor.CanHandle(url).Returns(false);

        await CreateSut().HandleAsync(Message(), CancellationToken.None);

        // Random github / news links must not cost a disk lookup on every message.
        await _cache.DidNotReceiveWithAnyArgs().TryGetAsync(default!, default);
    }

    [Fact]
    public async Task HandleAsync_ExtractionFails_NothingIsCached()
    {
        var url = new Uri("https://example.com/x");

        _urlExtractor.Extract(Arg.Any<string>()).Returns([url]);
        _extractor.CanHandle(url).Returns(true);
        _extractor.ExtractAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result<MediaPayload, ExtractionError>.Failure(new ExtractionError.NetworkFailure(url, "boom")));

        await CreateSut().HandleAsync(Message(), CancellationToken.None);

        // Caching a failure would make it stick for the whole lifetime of an entry — a platform
        // hiccup must not turn into a day of silence for that link.
        await _cache.DidNotReceiveWithAnyArgs().SaveAsync(default!, default!, default);
    }

    [Fact]
    public async Task HandleAsync_CancellationRequested_StopsProcessing()
    {
        var u1 = new Uri("https://example.com/1");
        var u2 = new Uri("https://example.com/2");
        var item = new MediaItem("/tmp/x.mp4", MediaKind.Video, null, null, null);
        var p1 = new MediaPayload(u1, null, null, [item]);

        _urlExtractor.Extract(Arg.Any<string>()).Returns([u1, u2]);
        _extractor.CanHandle(Arg.Any<Uri>()).Returns(true);
        _extractor.ExtractAsync(u1, Arg.Any<CancellationToken>())
            .Returns(Result<MediaPayload, ExtractionError>.Success(p1));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await CreateSut().HandleAsync(Message(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        await _extractor.DidNotReceiveWithAnyArgs().ExtractAsync(default!, default);
    }

    [Fact]
    public async Task HandleAsync_ExtractorSucceeds_JournalsMediaRepostWithCountAndChat()
    {
        var url = new Uri("https://example.com/x");
        var item = new MediaItem("/tmp/x.mp4", MediaKind.Video, "video/mp4", 100, 5);
        var payload = new MediaPayload(url, "title", "author", [item]);

        _urlExtractor.Extract(Arg.Any<string>()).Returns([url]);
        _extractor.CanHandle(url).Returns(true);
        _extractor.ExtractAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result<MediaPayload, ExtractionError>.Success(payload));

        await CreateSut().HandleAsync(Message(), CancellationToken.None);

        // The journal is the durable record behind the dashboard; one media item, 100 bytes, chat 123.
        await _journal.Received(1).RecordMediaRepostAsync(
            url, Arg.Any<string>(), 1, 100, Arg.Any<TimeSpan>(), 123L, 555L, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_MediaWithUnknownItemSize_JournalsNullBytes()
    {
        var url = new Uri("https://example.com/x");
        var item = new MediaItem("/tmp/x.mp4", MediaKind.Video, "video/mp4", SizeBytes: null, DurationSeconds: null);
        var payload = new MediaPayload(url, null, null, [item]);

        _urlExtractor.Extract(Arg.Any<string>()).Returns([url]);
        _extractor.CanHandle(url).Returns(true);
        _extractor.ExtractAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result<MediaPayload, ExtractionError>.Success(payload));

        await CreateSut().HandleAsync(Message(), CancellationToken.None);

        // Unknown size must journal as null, not a misleading 0 — otherwise bandwidth stats undercount.
        await _journal.Received(1).RecordMediaRepostAsync(
            url, Arg.Any<string>(), 1, null, Arg.Any<TimeSpan>(), 123L, 555L, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ExtractorFails_JournalsFailureCarryingTheError()
    {
        var url = new Uri("https://example.com/x");
        var error = new ExtractionError.ContentUnavailable(url, "gone");

        _urlExtractor.Extract(Arg.Any<string>()).Returns([url]);
        _extractor.CanHandle(url).Returns(true);
        _extractor.ExtractAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result<MediaPayload, ExtractionError>.Failure(error));

        await CreateSut().HandleAsync(Message(), CancellationToken.None);

        await _journal.Received(1).RecordFailureAsync(
            url, Arg.Any<string>(), error, Arg.Any<TimeSpan>(), 123L, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_NoMediaButDescription_JournalsTextFallback()
    {
        var url = new Uri("https://example.com/x");
        var payload = new MediaPayload(url, null, "saab", [], Description: "body text");

        _urlExtractor.Extract(Arg.Any<string>()).Returns([url]);
        _extractor.CanHandle(url).Returns(true);
        _extractor.ExtractAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result<MediaPayload, ExtractionError>.Success(payload));

        await CreateSut().HandleAsync(Message(), CancellationToken.None);

        await _journal.Received(1).RecordTextFallbackAsync(
            url, Arg.Any<string?>(), Arg.Any<TimeSpan>(), 123L, 555L, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_EmptyPayload_JournalsNothingExtracted()
    {
        var url = new Uri("https://example.com/x");
        var payload = new MediaPayload(url, null, null, []);

        _urlExtractor.Extract(Arg.Any<string>()).Returns([url]);
        _extractor.CanHandle(url).Returns(true);
        _extractor.ExtractAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result<MediaPayload, ExtractionError>.Success(payload));

        await CreateSut().HandleAsync(Message(), CancellationToken.None);

        await _journal.Received(1).RecordNothingExtractedAsync(
            url, Arg.Any<string>(), Arg.Any<TimeSpan>(), 123L, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_AllExtractorsUnsupported_JournalsNoExtractor()
    {
        var url = new Uri("https://github.com/user/repo");
        _urlExtractor.Extract(Arg.Any<string>()).Returns([url]);
        _extractor.CanHandle(url).Returns(true);
        _extractor.ExtractAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result<MediaPayload, ExtractionError>.Failure(new ExtractionError.UnsupportedPlatform(url)));

        await CreateSut().HandleAsync(Message(), CancellationToken.None);

        await _journal.Received(1).RecordNoExtractorAsync(
            url, Arg.Any<TimeSpan>(), 123L, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_NoCandidateExtractor_JournalsNothing()
    {
        var url = new Uri("https://news.example.com/article");
        _urlExtractor.Extract(Arg.Any<string>()).Returns([url]);
        _extractor.CanHandle(url).Returns(false);

        await CreateSut().HandleAsync(Message(), CancellationToken.None);

        // A bare link no extractor claims is pure noise — it must not produce a journal row at all.
        await _journal.DidNotReceiveWithAnyArgs().RecordNoExtractorAsync(default!, default, default, default);
        await _journal.DidNotReceiveWithAnyArgs().RecordFailureAsync(default!, default!, default!, default, default, default);
    }
}
