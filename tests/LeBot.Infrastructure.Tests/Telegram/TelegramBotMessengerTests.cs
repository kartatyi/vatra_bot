using System.Net;
using System.Net.Sockets;
using System.Text;
using LeBot.Domain.Media;
using LeBot.Infrastructure.Telegram;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;

namespace LeBot.Infrastructure.Tests.Telegram;

/// <summary>
/// Who owns the bytes after a send, and what the chat gets when the send fails. Both are cheap to get
/// wrong and expensive to notice: deleting the media cache's own copies turns every cached repost back
/// into a full extraction, and an upload failure that escapes leaves the chat silent with no journal row.
/// </summary>
public sealed class TelegramBotMessengerTests : IDisposable
{
    private static readonly Uri Source = new("https://example.com/x");

    private const string SentMessageJson = """{"message_id":1,"date":0,"chat":{"id":1,"type":"group"}}""";

    private readonly ITelegramBotClient _bot = Substitute.For<ITelegramBotClient>();

    // The failure tests run the real Telegram.Bot client over a faked wire: how it wraps a dropped
    // connection, and that it disposes the upload stream, is exactly the behaviour under test.
    private readonly FakeTelegramApi _api = new();
    private readonly HttpClient _http;

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "lebot-messenger-tests", Guid.NewGuid().ToString("N"));

    public TelegramBotMessengerTests()
    {
        Directory.CreateDirectory(_dir);
        _http = new HttpClient(_api);
    }

    [Fact]
    public async Task ReplyWithMediaAsync_DownloadedFile_IsDeletedOnceSent()
    {
        var file = WriteFile("clip.mp4");
        var payload = new MediaPayload(Source, null, null, [Video(file)]);

        await CreateSut().ReplyWithMediaAsync(1L, 2, payload, CancellationToken.None);

        // The upload really happened — the file wasn't deleted by an early throw skipping to finally.
        await _bot.Received(1).SendRequest(Arg.Any<IRequest<Message>>(), Arg.Any<CancellationToken>());
        File.Exists(file).Should().BeFalse();
    }

    [Fact]
    public async Task ReplyWithMediaAsync_CachedFile_SurvivesTheSend()
    {
        var file = WriteFile("clip.mp4");
        var payload = new MediaPayload(Source, null, null, [Video(file)], RetainFiles: true);

        await CreateSut().ReplyWithMediaAsync(1L, 2, payload, CancellationToken.None);

        File.Exists(file).Should().BeTrue();
    }

    [Fact]
    public async Task ReplyWithMediaAsync_DownloadedAlbum_IsDeletedOnceSent()
    {
        var first = WriteFile("a.jpg");
        var second = WriteFile("b.jpg");
        var payload = new MediaPayload(Source, null, null, [Photo(first), Photo(second)]);

        await CreateSut().ReplyWithMediaAsync(1L, 2, payload, CancellationToken.None);

        await _bot.Received(1).SendRequest(Arg.Any<IRequest<Message[]>>(), Arg.Any<CancellationToken>());
        File.Exists(first).Should().BeFalse();
        File.Exists(second).Should().BeFalse();
    }

    [Fact]
    public async Task ReplyWithMediaAsync_CachedAlbum_SurvivesTheSend()
    {
        var first = WriteFile("a.jpg");
        var second = WriteFile("b.jpg");
        var payload = new MediaPayload(Source, null, null, [Photo(first), Photo(second)], RetainFiles: true);

        await CreateSut().ReplyWithMediaAsync(1L, 2, payload, CancellationToken.None);

        File.Exists(first).Should().BeTrue();
        File.Exists(second).Should().BeTrue();
    }

    [Fact]
    public async Task ReplyWithMediaAsync_ConnectionDropsOnce_RetriesWithTheWholeFile()
    {
        var payload = new MediaPayload(Source, null, null, [Video(WriteFile("clip.mp4"))]);
        _api.Respond = (method, call) => call == 1 ? throw ConnectionReset() : Sent(method);

        var error = await CreateWireSut().ReplyWithMediaAsync(1L, 2, payload, CancellationToken.None);

        error.Should().BeNull();
        // Telegram.Bot closes the upload stream when a request fails; a retry reusing it would have nothing to send.
        _api.BodiesOf("sendVideo").Should().HaveCount(2)
            .And.OnlyContain(body => body.Contains("payload of clip.mp4"));
        _api.BodiesOf("sendMessage").Should().BeEmpty();
    }

    [Fact]
    public async Task ReplyWithMediaAsync_TelegramServerErrorOnce_RetriesWithTheWholeAlbum()
    {
        var payload = new MediaPayload(Source, null, null, [Video(WriteFile("a.mp4")), Video(WriteFile("b.mp4"))]);
        _api.Respond = (method, call) => call == 1 ? Refused(502, "Bad Gateway") : Sent(method);

        var error = await CreateWireSut().ReplyWithMediaAsync(1L, 2, payload, CancellationToken.None);

        error.Should().BeNull();
        _api.BodiesOf("sendMediaGroup").Should().HaveCount(2)
            .And.OnlyContain(body => body.Contains("payload of a.mp4") && body.Contains("payload of b.mp4"));
    }

    [Fact]
    public async Task ReplyWithMediaAsync_AlbumConnectionKeepsDropping_RepliesWithSourceLinkAndReportsUnreachable()
    {
        var payload = new MediaPayload(Source, null, null, [Video(WriteFile("a.mp4")), Video(WriteFile("b.mp4"))]);
        _api.Respond = (method, _) => method == "sendMediaGroup" ? throw ConnectionReset() : Sent(method);

        var error = await CreateWireSut().ReplyWithMediaAsync(1L, 2, payload, CancellationToken.None);

        // A reset album upload used to escape to the dispatcher: no retry, no reply, no journal row.
        error.Should().BeOfType<DeliveryError.TelegramUnreachable>()
            .Which.Detail.Should().Contain("Error while copying content to a stream.");
        _api.BodiesOf("sendMediaGroup").Should().HaveCount(4, "a dropped connection gets the same three retries as a 5xx");
        _api.BodiesOf("sendMessage").Should().ContainSingle()
            .Which.Should().Contain(Source.ToString());
    }

    [Fact]
    public async Task ReplyWithMediaAsync_TelegramRefusesUpload_RepliesWithSourceLinkWithoutRetrying()
    {
        var payload = new MediaPayload(Source, null, null, [Video(WriteFile("clip.mp4"))]);
        _api.Respond = (method, _) => method == "sendVideo" ? Refused(400, "Bad Request: file is too big") : Sent(method);

        var error = await CreateWireSut().ReplyWithMediaAsync(1L, 2, payload, CancellationToken.None);

        error.Should().Be(new DeliveryError.TelegramRefused(400, "Bad Request: file is too big"));
        _api.BodiesOf("sendVideo").Should().ContainSingle();
        _api.BodiesOf("sendMessage").Should().ContainSingle()
            .Which.Should().Contain(Source.ToString());
    }

    [Fact]
    public async Task ReplyWithMediaAsync_UploadTimesOut_FallsBackWithoutRetrying()
    {
        var payload = new MediaPayload(Source, null, null, [Video(WriteFile("clip.mp4"))]);
        // HttpClient reports a cancellation nobody asked for as its own timeout — the real 100 s case.
        _api.Respond = (method, _) => method == "sendVideo" ? throw new TaskCanceledException() : Sent(method);

        var error = await CreateWireSut().ReplyWithMediaAsync(1L, 2, payload, CancellationToken.None);

        error.Should().BeOfType<DeliveryError.TelegramUnreachable>();
        _api.BodiesOf("sendVideo").Should().ContainSingle("a timeout has already spent the whole upload budget");
        _api.BodiesOf("sendMessage").Should().ContainSingle();
    }

    [Fact]
    public async Task ReplyWithMediaAsync_SourceLinkFailsToo_StillReportsTheUploadFailure()
    {
        var payload = new MediaPayload(Source, null, null, [Video(WriteFile("clip.mp4"))]);
        _api.Respond = (_, _) => throw ConnectionReset();

        var error = await CreateWireSut().ReplyWithMediaAsync(1L, 2, payload, CancellationToken.None);

        // Returned, not thrown: the handler has to journal it for the failure to be seen at all.
        error.Should().BeOfType<DeliveryError.TelegramUnreachable>();
    }

    private TelegramBotMessenger CreateSut() =>
        new(_bot, TimeProvider.System, NullLogger<TelegramBotMessenger>.Instance);

    private TelegramBotMessenger CreateWireSut() =>
        new(new TelegramBotClient("123456:not-a-real-token", _http), new InstantTimeProvider(), NullLogger<TelegramBotMessenger>.Instance);

    private static MediaItem Video(string path) => new(path, MediaKind.Video, "video/mp4", null, 5);

    private static MediaItem Photo(string path) => new(path, MediaKind.Photo, "image/jpeg", null, null);

    /// <summary>The exception chain from the 2026-09-15 log, minus the OS-specific socket message.</summary>
    private static HttpRequestException ConnectionReset() =>
        new("Error while copying content to a stream.",
            new IOException("Unable to write data to the transport connection.",
                new SocketException((int)SocketError.ConnectionReset)));

    private static HttpResponseMessage Sent(string method) =>
        Json(HttpStatusCode.OK, $$"""{"ok":true,"result":{{(method == "sendMediaGroup" ? $"[{SentMessageJson}]" : SentMessageJson)}}}""");

    private static HttpResponseMessage Refused(int code, string description) =>
        Json((HttpStatusCode)code, $$"""{"ok":false,"error_code":{{code}},"description":"{{description}}"}""");

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private string WriteFile(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, $"payload of {name}");
        return path;
    }

    public void Dispose()
    {
        _http.Dispose();
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException) { /* best-effort temp cleanup */ }
    }

    /// <summary>
    /// Telegram's side of the wire. Drains each request body the way a socket would — an attempt holding a
    /// closed file fails right there — then answers with whatever the test scripted for that Bot API method
    /// and its 1-based call number.
    /// </summary>
    private sealed class FakeTelegramApi : HttpMessageHandler
    {
        private readonly List<(string Method, string Body)> _requests = [];

        public Func<string, int, HttpResponseMessage> Respond { get; set; } = (method, _) => Sent(method);

        public IReadOnlyList<string> BodiesOf(string method) =>
            [.. _requests.Where(r => r.Method == method).Select(r => r.Body)];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var method = request.RequestUri?.Segments[^1] ?? "";
            _requests.Add((method, body));
            return Respond(method, BodiesOf(method).Count);
        }
    }

    /// <summary>Fires every timer at once, so the retry backoff costs the test suite nothing.</summary>
    private sealed class InstantTimeProvider : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                ThreadPool.QueueUserWorkItem(_ => callback(state));
            }

            return new InertTimer();
        }

        private sealed class InertTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose() { }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
