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
    private readonly ITelegramMessenger _messenger = Substitute.For<ITelegramMessenger>();
    private readonly ILogger<HandleIncomingMessageHandler> _logger = NullLogger<HandleIncomingMessageHandler>.Instance;

    private HandleIncomingMessageHandler CreateSut() =>
        new(_urlExtractor, [_extractor], _messenger, _logger);

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
    public async Task HandleAsync_EmptyPayload_SkipsMessenger()
    {
        var url = new Uri("https://example.com/x");
        var payload = new MediaPayload(url, null, null, []);

        _urlExtractor.Extract(Arg.Any<string>()).Returns([url]);
        _extractor.CanHandle(url).Returns(true);
        _extractor.ExtractAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result<MediaPayload, ExtractionError>.Success(payload));

        await CreateSut().HandleAsync(Message(), CancellationToken.None);

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
    public async Task HandleAsync_ExtractorFails_DoesNotCallMessenger()
    {
        var url = new Uri("https://example.com/x");
        var error = new ExtractionError.ContentUnavailable(url, "gone");

        _urlExtractor.Extract(Arg.Any<string>()).Returns([url]);
        _extractor.CanHandle(url).Returns(true);
        _extractor.ExtractAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result<MediaPayload, ExtractionError>.Failure(error));

        await CreateSut().HandleAsync(Message(), CancellationToken.None);

        await _messenger.DidNotReceiveWithAnyArgs().ReplyWithMediaAsync(default, default, default!, default);
        await _messenger.DidNotReceiveWithAnyArgs().ReplyWithTextAsync(default, default, default!, default);
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
