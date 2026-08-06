using LeBot.Domain.Media;
using LeBot.Infrastructure.Telegram;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot;
using Telegram.Bot.Requests;
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

    [Fact]
    public async Task ReplyWithMediaAsync_TextOnlyChain_ArrivesAsOneMessageAfterTheMedia()
    {
        var payload = new MediaPayload(Source, null, null, [Video(WriteFile("clip.mp4"))])
        {
            FollowUps = [Text("part two"), Text("part three"), Text("part four")],
        };

        await CreateSut().ReplyWithMediaAsync(1L, 2, payload, CancellationToken.None);

        // One media send, then one text send carrying all three parts — not three pings.
        SentRequests().Should().Equal("SendVideoRequest", "SendMessageRequest");
        SentTexts().Should().ContainSingle().Which.Should().Be("part two\n\npart three\n\npart four");
    }

    [Fact]
    public async Task ReplyWithMediaAsync_ChainWithItsOwnMedia_BreaksTheTextRunAroundIt()
    {
        var partPhoto = WriteFile("part.jpg");
        var payload = new MediaPayload(Source, null, null, [Video(WriteFile("clip.mp4"))])
        {
            FollowUps =
            [
                Text("part two"),
                new PostSegment("part three, with a picture", [Photo(partPhoto)]),
                Text("part four"),
            ],
        };

        await CreateSut().ReplyWithMediaAsync(1L, 2, payload, CancellationToken.None);

        SentRequests().Should().Equal(
            "SendVideoRequest", "SendMessageRequest", "SendPhotoRequest", "SendMessageRequest");
        SentTexts().Should().Equal("part two", "part four");
        File.Exists(partPhoto).Should().BeFalse();
    }

    [Fact]
    public async Task ReplyWithMediaAsync_ChainLongerThanOneMessage_SplitsBetweenParts()
    {
        var first = new string('a', 3000);
        var second = new string('b', 3000);
        var payload = new MediaPayload(Source, null, null, [Video(WriteFile("clip.mp4"))])
        {
            FollowUps = [Text(first), Text(second)],
        };

        await CreateSut().ReplyWithMediaAsync(1L, 2, payload, CancellationToken.None);

        // 6000 chars can't be one message, and neither part may be cut: two messages, whole parts.
        SentTexts().Should().Equal(first, second);
    }

    [Fact]
    public async Task ReplyWithTextAsync_TextOnlyPostWithAChain_SendsBodyThenTheParts()
    {
        var payload = new MediaPayload(Source, null, null, [], "the opening post")
        {
            FollowUps = [Text("part two")],
        };

        await CreateSut().ReplyWithTextAsync(1L, 2, payload, CancellationToken.None);

        SentTexts().Should().Equal("the opening post", "part two");
    }

    [Fact]
    public async Task ReplyWithMediaAsync_CachedChainMedia_SurvivesTheSend()
    {
        var partPhoto = WriteFile("part.jpg");
        var payload = new MediaPayload(Source, null, null, [Video(WriteFile("clip.mp4"))], RetainFiles: true)
        {
            FollowUps = [new PostSegment("part two", [Photo(partPhoto)])],
        };

        await CreateSut().ReplyWithMediaAsync(1L, 2, payload, CancellationToken.None);

        File.Exists(partPhoto).Should().BeTrue();
    }

    /// <summary>Every request the bot was asked to make, by type name, in order.</summary>
    private List<string> SentRequests() =>
        _bot.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(ITelegramBotClient.SendRequest))
            .Select(call => call.GetArguments()[0]!.GetType().Name)
            .ToList();

    /// <summary>The text of every SendMessage the bot was asked to make, in order.</summary>
    private List<string> SentTexts() =>
        _bot.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(ITelegramBotClient.SendRequest))
            .Select(call => call.GetArguments()[0])
            .OfType<SendMessageRequest>()
            .Select(request => request.Text)
            .ToList();

    private static PostSegment Text(string text) => new(text, []);

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
