using LeBot.Domain.Media;
using LeBot.Infrastructure.Telegram;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;

namespace LeBot.Infrastructure.Tests.Telegram;

/// <summary>
/// Who owns the bytes after a send. Cheap to get wrong and expensive to notice: deleting the media
/// cache's own copies turns every cached repost back into a full extraction, silently.
/// </summary>
public sealed class TelegramBotMessengerTests : IDisposable
{
    private static readonly Uri Source = new("https://example.com/x");

    private readonly ITelegramBotClient _bot = Substitute.For<ITelegramBotClient>();

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "lebot-messenger-tests", Guid.NewGuid().ToString("N"));

    public TelegramBotMessengerTests() => Directory.CreateDirectory(_dir);

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

    private TelegramBotMessenger CreateSut() =>
        new(_bot, NullLogger<TelegramBotMessenger>.Instance);

    private static MediaItem Video(string path) => new(path, MediaKind.Video, "video/mp4", null, 5);

    private static MediaItem Photo(string path) => new(path, MediaKind.Photo, "image/jpeg", null, null);

    private string WriteFile(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "bytes");
        return path;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException) { /* best-effort temp cleanup */ }
    }
}
