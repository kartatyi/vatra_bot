using LeBot.Domain.Media;
using LeBot.Infrastructure.Configuration;
using LeBot.Infrastructure.MediaCache;
using LeBot.Infrastructure.Persistence;
using LeBot.Infrastructure.Telegram;
using LeBot.Infrastructure.Tests.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;

namespace LeBot.Infrastructure.Tests.MediaCache;

public sealed class TelegramMediaCacheTests : IDisposable
{
    private const long Storage = -1001;
    private static readonly Uri Url = new("https://www.tiktok.com/@a/video/1?_r=1");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lebot-tgcache-" + Path.GetRandomFileName());
    private readonly ITelegramBotClient _bot = Substitute.For<ITelegramBotClient>();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
    private readonly DbContextOptions<LeBotDbContext> _db;

    public TelegramMediaCacheTests()
    {
        Directory.CreateDirectory(_dir);
        _db = new DbContextOptionsBuilder<LeBotDbContext>().UseSqlite($"Data Source={Path.Combine(_dir, "t.db")}").Options;
        using var db = new LeBotDbContext(_db);
        db.Database.Migrate();

        _bot.SendRequest(Arg.Any<SendVideoRequest>(), Arg.Any<CancellationToken>())
            .Returns(new Message { Video = new Video { FileId = "vid-1", FileUniqueId = "u1" } });
        _bot.SendRequest(Arg.Any<SendMediaGroupRequest>(), Arg.Any<CancellationToken>())
            .Returns(
            [
                new Message { Photo = [new PhotoSize { FileId = "small" }, new PhotoSize { FileId = "ph-1" }] },
                new Message { Photo = [new PhotoSize { FileId = "ph-2" }] },
            ]);
    }

    [Fact]
    public async Task Save_Video_UploadsToStorageOnce_DeletesTheDownload_AndHandsBackTheStoredPost()
    {
        var file = Download("clip.mp4");
        var payload = new MediaPayload(Url, null, "a", [new MediaItem(file, MediaKind.Video, "video/mp4", 10, 5)], "post");

        var toSend = await Sut().SaveAsync(payload, "YtDlpPlatformExtractor", CancellationToken.None);

        await _bot.Received(1).SendRequest(Arg.Is<SendVideoRequest>(r => r.ChatId == Storage), Arg.Any<CancellationToken>());
        File.Exists(file).Should().BeFalse();
        toSend.RetainFiles.Should().BeTrue();
        toSend.Items.Should().ContainSingle().Which.Should().BeEquivalentTo(new { FilePath = "", TelegramFileId = "vid-1", Kind = MediaKind.Video });
        toSend.Description.Should().Be("post");
    }

    [Fact]
    public async Task TryGet_AfterSave_ServesTheStoredPost_AcrossShareVariants()
    {
        var sut = Sut();
        await sut.SaveAsync(Video(), "YtDlpPlatformExtractor", CancellationToken.None);

        var hit = await sut.TryGetAsync(new Uri("https://tiktok.com/@a/video/1?_r=2"), CancellationToken.None);

        hit!.Extractor.Should().Be("YtDlpPlatformExtractor");
        hit.Payload.RetainFiles.Should().BeTrue();
        hit.Payload.Items.Single().TelegramFileId.Should().Be("vid-1");
        hit.Payload.Author.Should().Be("a");
    }

    [Fact]
    public async Task Save_Album_GoesUpAsOneGroup()
    {
        var payload = new MediaPayload(Url, null, null,
            [new MediaItem(Download("1.jpg"), MediaKind.Photo, null, null, null), new MediaItem(Download("2.jpg"), MediaKind.Photo, null, null, null)]);

        var toSend = await Sut().SaveAsync(payload, "InstagramApiExtractor", CancellationToken.None);

        toSend.Items.Select(i => i.TelegramFileId).Should().Equal("ph-1", "ph-2");
        await _bot.Received(1).SendRequest(Arg.Any<SendMediaGroupRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Save_TextOnly_IsStoredWithoutTouchingTelegram()
    {
        var sut = Sut();
        await sut.SaveAsync(new MediaPayload(Url, null, null, [], "words"), "ThreadsPostExtractor", CancellationToken.None);

        var hit = await sut.TryGetAsync(Url, CancellationToken.None);

        hit!.Payload.Description.Should().Be("words");
        hit.Payload.HasMedia.Should().BeFalse();
        _bot.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Save_UploadFails_HandsBackTheOriginal_KeepsTheFile_StoresNothing()
    {
        _bot.SendRequest(Arg.Any<SendVideoRequest>(), Arg.Any<CancellationToken>())
            .Returns<Message>(_ => throw new ApiRequestException("Too Many Requests", 429));
        var payload = Video();
        var sut = Sut();

        var toSend = await sut.SaveAsync(payload, "YtDlpPlatformExtractor", CancellationToken.None);

        toSend.Should().BeSameAs(payload);
        File.Exists(payload.Items[0].FilePath).Should().BeTrue();
        (await sut.TryGetAsync(Url, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task TryGet_PastItsLifetime_IsAMiss()
    {
        var sut = Sut();
        await sut.SaveAsync(Video(), "YtDlpPlatformExtractor", CancellationToken.None);
        _time.Advance(TimeSpan.FromHours(25));

        (await sut.TryGetAsync(Url, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Save_PayloadAlreadyFromAStore_IsHandedBackUntouched()
    {
        var stored = Video() with { RetainFiles = true };

        (await Sut().SaveAsync(stored, "MediaCache", CancellationToken.None)).Should().BeSameAs(stored);
        _bot.ReceivedCalls().Should().BeEmpty();
    }

    private MediaPayload Video() =>
        new(Url, null, "a", [new MediaItem(Download("clip.mp4"), MediaKind.Video, "video/mp4", 10, 5)], "post");

    private string Download(string name)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + "-" + name);
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    private TelegramMediaCache Sut()
    {
        var vault = new TelegramMediaVault(_bot, Options.Create(new StorageOptions { ChatId = Storage }), NullLogger<TelegramMediaVault>.Instance);
        return new TelegramMediaCache(
            vault,
            new TestDbContextFactory(_db),
            Options.Create(new MediaCacheOptions { TtlHours = 24 }),
            _time,
            NullLogger<TelegramMediaCache>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }
}
