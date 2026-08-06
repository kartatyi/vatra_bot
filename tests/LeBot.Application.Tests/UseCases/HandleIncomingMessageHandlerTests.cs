using LeBot.Application.Caching;
using LeBot.Application.Metrics;
using LeBot.Application.Ports;
using LeBot.Application.UseCases.HandleIncomingMessage;
using LeBot.Domain.Common;
using LeBot.Domain.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LeBot.Application.Tests.UseCases;

public class HandleIncomingMessageHandlerTests
{
    private readonly IUrlExtractor _urlExtractor = Substitute.For<IUrlExtractor>();
    private readonly IPlatformExtractor _extractor = Substitute.For<IPlatformExtractor>();
    private readonly IMediaCache _cache = Substitute.For<IMediaCache>();
    private readonly ITelegramMessenger _messenger = Substitute.For<ITelegramMessenger>();
    private readonly RepostMetrics _metrics = new(TimeProvider.System);
    private readonly ILogger<HandleIncomingMessageHandler> _logger = NullLogger<HandleIncomingMessageHandler>.Instance;

    private HandleIncomingMessageHandler CreateSut() =>
        new(_urlExtractor, [_extractor], _cache, _messenger, _metrics, _logger);

    private static IncomingMessage Message(string text = "hi") =>
        new(ChatId: 123L, MessageId: 7, Text: text, SenderUsername: "user");

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

        var sut = new HandleIncomingMessageHandler(_urlExtractor, [first, second], _cache, _messenger, _metrics, _logger);
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

        var sut = new HandleIncomingMessageHandler(_urlExtractor, [first, second], _cache, _messenger, _metrics, _logger);
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
}
