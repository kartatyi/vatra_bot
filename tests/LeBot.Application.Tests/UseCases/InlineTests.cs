using LeBot.Application.Caching;
using LeBot.Application.Ports;
using LeBot.Application.UseCases.AnswerInlineQuery;
using LeBot.Domain.Common;
using LeBot.Domain.Media;
using Microsoft.Extensions.Logging.Abstractions;

namespace LeBot.Application.Tests.UseCases;

public class InlineTests
{
    private static readonly Uri Url = new("https://www.tiktok.com/@a/video/1");

    private readonly IUrlExtractor _urls = Substitute.For<IUrlExtractor>();
    private readonly IMediaCache _cache = Substitute.For<IMediaCache>();
    private readonly IPlatformExtractor _extractor = Substitute.For<IPlatformExtractor>();
    private readonly IInlineMediaUploader _uploader = Substitute.For<IInlineMediaUploader>();
    private readonly IPreparedPostStore _store = Substitute.For<IPreparedPostStore>();
    private readonly IInlineAnswerer _answerer = Substitute.For<IInlineAnswerer>();
    private readonly IInlineMessageEditor _editor = Substitute.For<IInlineMessageEditor>();
    private readonly FakeTimeProvider _time = new();

    public InlineTests()
    {
        _urls.Extract(Arg.Any<string>()).Returns([Url]);
        _extractor.CanHandle(Url).Returns(true);
        _cache.SaveAsync(Arg.Any<MediaPayload>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<MediaPayload>());
    }

    [Fact]
    public async Task Handle_NoLinkInTheQuery_AnswersNoLink()
    {
        _urls.Extract("hello").Returns([]);

        await CreateHandler().HandleAsync(new InlineQuery("q", "hello"), CancellationToken.None);

        await _answerer.Received(1).AnswerAsync("q", new InlineAnswer.NoLink(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_AlreadyPrepared_AnswersReadyWithoutFetching()
    {
        var post = new PreparedPost(MediaKind.Video, "file-1", "body");
        _store.TryGetAsync(Url, Arg.Any<CancellationToken>()).Returns(post);

        await CreateHandler().HandleAsync(new InlineQuery("q", Url.AbsoluteUri), CancellationToken.None);

        await _answerer.Received(1).AnswerAsync("q", new InlineAnswer.Ready(post, Url), Arg.Any<CancellationToken>());
        await _extractor.DidNotReceiveWithAnyArgs().ExtractAsync(default!, default);
    }

    [Fact]
    public async Task Handle_NothingFetchable_AnswersFailed()
    {
        _extractor.ExtractAsync(Url, Arg.Any<CancellationToken>())
            .Returns(Result<MediaPayload, ExtractionError>.Failure(new ExtractionError.ContentUnavailable(Url, "gone")));

        await CreateHandler().HandleAsync(new InlineQuery("q", Url.AbsoluteUri), CancellationToken.None);

        await _answerer.Received(1).AnswerAsync("q", new InlineAnswer.Failed(Url), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_PreparationOutlastsTheBudget_AnswersNotReady()
    {
        _store.TryGetAsync(Url, Arg.Any<CancellationToken>()).Returns(new TaskCompletionSource<PreparedPost?>().Task);

        var handling = CreateHandler().HandleAsync(new InlineQuery("q", Url.AbsoluteUri), CancellationToken.None);
        _time.Advance(AnswerInlineQueryHandler.WaitBudget + TimeSpan.FromSeconds(1));
        await handling;

        await _answerer.Received(1).AnswerAsync("q", new InlineAnswer.NotReady(Url), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Prepare_Media_CachesUploadsAndStores()
    {
        var payload = new MediaPayload(Url, null, "a", [new MediaItem("/tmp/x.mp4", MediaKind.Video, null, null, null)], "clip");
        _extractor.ExtractAsync(Url, Arg.Any<CancellationToken>()).Returns(Result<MediaPayload, ExtractionError>.Success(payload));
        _uploader.UploadAsync(payload, Arg.Any<CancellationToken>()).Returns((MediaKind.Video, "file-1"));

        var post = await CreatePreparer().PrepareAsync(Url);

        var expected = new PreparedPost(MediaKind.Video, "file-1", payload.Body);
        post.Should().Be(expected);
        Received.InOrder(() =>
        {
            _cache.SaveAsync(payload, Arg.Any<string>(), Arg.Any<CancellationToken>());
            _uploader.UploadAsync(payload, Arg.Any<CancellationToken>());
            _store.SaveAsync(Url, expected, Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task Prepare_CacheHit_UploadsTheCachedPayloadWithoutExtracting()
    {
        var payload = new MediaPayload(Url, null, null, [new MediaItem("/cache/00.mp4", MediaKind.Video, null, null, null)], RetainFiles: true);
        _cache.TryGetAsync(Url, Arg.Any<CancellationToken>()).Returns(new CachedRepost(payload, "YtDlpPlatformExtractor"));
        _uploader.UploadAsync(payload, Arg.Any<CancellationToken>()).Returns((MediaKind.Video, "file-2"));

        var post = await CreatePreparer().PrepareAsync(Url);

        post!.FileId.Should().Be("file-2");
        await _extractor.DidNotReceiveWithAnyArgs().ExtractAsync(default!, default);
    }

    [Fact]
    public async Task Prepare_TextOnlyPost_PreparesTextWithoutUploading()
    {
        var payload = new MediaPayload(Url, null, null, [], "just words");
        _extractor.ExtractAsync(Url, Arg.Any<CancellationToken>()).Returns(Result<MediaPayload, ExtractionError>.Success(payload));

        var post = await CreatePreparer().PrepareAsync(Url);

        post.Should().Be(new PreparedPost(Kind: null, FileId: null, "just words"));
        await _uploader.DidNotReceiveWithAnyArgs().UploadAsync(default!, default);
    }

    [Fact]
    public async Task Prepare_UploadFails_PreparesNothingAndStoresNothing()
    {
        var payload = new MediaPayload(Url, null, null, [new MediaItem("/tmp/x.mp4", MediaKind.Video, null, null, null)]);
        _extractor.ExtractAsync(Url, Arg.Any<CancellationToken>()).Returns(Result<MediaPayload, ExtractionError>.Success(payload));
        _uploader.UploadAsync(payload, Arg.Any<CancellationToken>()).Returns(((MediaKind, string)?)null);

        var post = await CreatePreparer().PrepareAsync(Url);

        post.Should().BeNull();
        await _store.DidNotReceiveWithAnyArgs().SaveAsync(default!, default!, default);
    }

    [Fact]
    public async Task Prepare_ExtractorThrows_PreparesNothing()
    {
        _extractor.ExtractAsync(Url, Arg.Any<CancellationToken>())
            .Returns<Result<MediaPayload, ExtractionError>>(_ => throw new InvalidOperationException("boom"));

        var post = await CreatePreparer().PrepareAsync(Url);

        post.Should().BeNull();
    }

    [Fact]
    public async Task Prepare_ConcurrentCallsForOneLink_ShareOneRun()
    {
        var pending = new TaskCompletionSource<PreparedPost?>();
        _store.TryGetAsync(Url, Arg.Any<CancellationToken>()).Returns(pending.Task);
        var preparer = CreatePreparer();

        var first = preparer.PrepareAsync(Url);
        var second = preparer.PrepareAsync(Url);
        pending.SetResult(new PreparedPost(MediaKind.Photo, "file-3", null));

        (await first).Should().Be(await second);
        await _store.Received(1).TryGetAsync(Url, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CompletePlaceholder_PreparedPost_ReplacesThePlaceholder(bool withText)
    {
        var post = new PreparedPost(MediaKind.Video, "file-1", "body");
        _store.TryGetAsync(Url, Arg.Any<CancellationToken>()).Returns(post);

        await CreatePlaceholderHandler().HandleAsync(new ChosenPlaceholder("im-1", Url.AbsoluteUri, withText), CancellationToken.None);

        await _editor.Received(1).ShowPostAsync("im-1", post, withText, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompletePlaceholder_NothingFetchable_ShowsTheFailure()
    {
        _extractor.ExtractAsync(Url, Arg.Any<CancellationToken>())
            .Returns(Result<MediaPayload, ExtractionError>.Failure(new ExtractionError.ContentUnavailable(Url, "gone")));

        await CreatePlaceholderHandler().HandleAsync(new ChosenPlaceholder("im-1", Url.AbsoluteUri, false), CancellationToken.None);

        await _editor.Received(1).ShowFailureAsync("im-1", Url, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompletePlaceholder_NoLink_LeavesItAlone()
    {
        _urls.Extract("hello").Returns([]);

        await CreatePlaceholderHandler().HandleAsync(new ChosenPlaceholder("im-1", "hello", false), CancellationToken.None);

        _editor.ReceivedCalls().Should().BeEmpty();
    }

    private CompleteInlinePlaceholderHandler CreatePlaceholderHandler() =>
        new(_urls, CreatePreparer(), _editor, NullLogger<CompleteInlinePlaceholderHandler>.Instance);

    private InlinePostPreparer CreatePreparer() =>
        new(_cache, [_extractor], _uploader, _store, _time, NullLogger<InlinePostPreparer>.Instance);

    private AnswerInlineQueryHandler CreateHandler() =>
        new(_urls, CreatePreparer(), _answerer, _time, NullLogger<AnswerInlineQueryHandler>.Instance);
}
