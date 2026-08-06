using LeBot.Domain.Media;
using LeBot.Infrastructure.Configuration;
using LeBot.Infrastructure.MediaCache;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LeBot.Infrastructure.Tests.MediaCache;

public sealed class FileSystemMediaCacheTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly Uri Url = new("https://tiktok.com/@user/video/123");

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "lebot-cache-tests", Guid.NewGuid().ToString("N"));

    private readonly string _downloads;
    private readonly FakeTimeProvider _clock = new(Now);

    public FileSystemMediaCacheTests()
    {
        _downloads = Path.Combine(_root, "downloads");
        Directory.CreateDirectory(_downloads);
    }

    [Fact]
    public async Task SaveAsync_ThenTryGetAsync_ServesTheStoredPayload()
    {
        var sut = CreateSut();
        var file = WriteDownload("clip.mp4", "video-bytes");
        var payload = new MediaPayload(Url, "title", "author", [Video(file)], "description");

        await sut.SaveAsync(payload, "YtDlpPlatformExtractor", CancellationToken.None);
        var hit = await sut.TryGetAsync(Url, CancellationToken.None);

        hit.Should().NotBeNull();
        hit!.Extractor.Should().Be("YtDlpPlatformExtractor");
        hit.Payload.SourceUrl.Should().Be(Url);
        hit.Payload.Title.Should().Be("title");
        hit.Payload.Author.Should().Be("author");
        hit.Payload.Description.Should().Be("description");
        hit.Payload.Items.Should().ContainSingle();

        var served = hit.Payload.Items[0];
        served.Kind.Should().Be(MediaKind.Video);
        served.MimeType.Should().Be("video/mp4");
        served.DurationSeconds.Should().Be(5);
        (await File.ReadAllTextAsync(served.FilePath)).Should().Be("video-bytes");
    }

    [Fact]
    public async Task TryGetAsync_ServedPayload_IsFlaggedSoTheSenderKeepsTheFiles()
    {
        var sut = CreateSut();
        var payload = new MediaPayload(Url, null, null, [Video(WriteDownload("clip.mp4", "bytes"))]);
        await sut.SaveAsync(payload, "YtDlpPlatformExtractor", CancellationToken.None);

        var hit = await sut.TryGetAsync(Url, CancellationToken.None);

        // Without this the messenger deletes the cache's own copy right after the first replay,
        // and every later repost of the link goes back to the platform.
        hit!.Payload.RetainFiles.Should().BeTrue();
        hit.Payload.Items[0].FilePath.Should().NotBe(payload.Items[0].FilePath);
    }

    [Fact]
    public async Task SaveAsync_TakesACopy_LeavingTheDownloadForTheSenderToDelete()
    {
        var sut = CreateSut();
        var file = WriteDownload("clip.mp4", "bytes");

        await sut.SaveAsync(
            new MediaPayload(Url, null, null, [Video(file)]),
            "YtDlpPlatformExtractor",
            CancellationToken.None);

        File.Exists(file).Should().BeTrue();
    }

    [Fact]
    public async Task TryGetAsync_UrlNeverSeen_IsAMiss()
    {
        var hit = await CreateSut().TryGetAsync(Url, CancellationToken.None);

        hit.Should().BeNull();
    }

    [Fact]
    public async Task TryGetAsync_SameContentBehindADifferentShareLink_StillHits()
    {
        var sut = CreateSut();
        await sut.SaveAsync(
            new MediaPayload(Url, null, null, [Video(WriteDownload("clip.mp4", "bytes"))]),
            "YtDlpPlatformExtractor",
            CancellationToken.None);

        var hit = await sut.TryGetAsync(
            new Uri("https://www.tiktok.com/@user/video/123/?_t=ZS-8x&_r=1"),
            CancellationToken.None);

        hit.Should().NotBeNull();
    }

    [Theory]
    [InlineData(23, true)]   // still inside the 24h lifetime
    [InlineData(24, false)]  // exactly at it — gone
    [InlineData(48, false)]
    public async Task TryGetAsync_EntryIsServedUntilItsLifetimeRunsOut(int hoursLater, bool shouldHit)
    {
        var sut = CreateSut();
        await sut.SaveAsync(
            new MediaPayload(Url, null, null, [Video(WriteDownload("clip.mp4", "bytes"))]),
            "YtDlpPlatformExtractor",
            CancellationToken.None);

        _clock.Advance(TimeSpan.FromHours(hoursLater));
        var hit = await sut.TryGetAsync(Url, CancellationToken.None);

        (hit is not null).Should().Be(shouldHit);
    }

    [Fact]
    public async Task TryGetAsync_ExpiredEntry_IsDeletedOnTheSpot()
    {
        var sut = CreateSut();
        await sut.SaveAsync(
            new MediaPayload(Url, null, null, [Video(WriteDownload("clip.mp4", "bytes"))]),
            "YtDlpPlatformExtractor",
            CancellationToken.None);

        _clock.Advance(TimeSpan.FromHours(25));
        await sut.TryGetAsync(Url, CancellationToken.None);

        // The disk is reclaimed the moment we notice, not only when the sweep next runs.
        Directory.GetDirectories(CacheRoot).Should().BeEmpty();
    }

    [Fact]
    public async Task TryGetAsync_MediaFileWentMissing_IsAMissRatherThanAPartialAlbum()
    {
        var sut = CreateSut();
        await sut.SaveAsync(
            new MediaPayload(Url, null, null, [Video(WriteDownload("a.mp4", "one")), Video(WriteDownload("b.mp4", "two"))]),
            "YtDlpPlatformExtractor",
            CancellationToken.None);

        var stored = Directory.GetDirectories(CacheRoot).Single();
        File.Delete(Directory.GetFiles(stored, "01.*").Single());

        var hit = await sut.TryGetAsync(Url, CancellationToken.None);

        hit.Should().BeNull();
        Directory.Exists(stored).Should().BeFalse();
    }

    [Fact]
    public async Task TryGetAsync_CorruptEntry_IsDiscarded()
    {
        var sut = CreateSut();
        await sut.SaveAsync(
            new MediaPayload(Url, null, null, [Video(WriteDownload("clip.mp4", "bytes"))]),
            "YtDlpPlatformExtractor",
            CancellationToken.None);

        var stored = Directory.GetDirectories(CacheRoot).Single();
        await File.WriteAllTextAsync(Path.Combine(stored, "entry.json"), "{ this is not json");

        var hit = await sut.TryGetAsync(Url, CancellationToken.None);

        hit.Should().BeNull();
        Directory.Exists(stored).Should().BeFalse();
    }

    [Fact]
    public async Task SaveAsync_PayloadThatCameFromTheCache_IsNotStoredAgain()
    {
        var sut = CreateSut();
        var fromCache = new MediaPayload(
            Url, null, null, [Video(WriteDownload("clip.mp4", "bytes"))], RetainFiles: true);

        await sut.SaveAsync(fromCache, "YtDlpPlatformExtractor", CancellationToken.None);

        // Re-saving would copy the files onto themselves and slide the expiry forward on every
        // repost — a popular link would then never age out.
        Directory.Exists(CacheRoot).Should().BeFalse();
    }

    [Fact]
    public async Task SaveAsync_Disabled_StoresNothingAndAlwaysMisses()
    {
        var sut = CreateSut(new MediaCacheOptions { Enabled = false, Directory = CacheRoot });
        var payload = new MediaPayload(Url, null, null, [Video(WriteDownload("clip.mp4", "bytes"))]);

        await sut.SaveAsync(payload, "YtDlpPlatformExtractor", CancellationToken.None);

        Directory.Exists(CacheRoot).Should().BeFalse();
        (await sut.TryGetAsync(Url, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task SaveAsync_MissingSourceFile_LeavesNoHalfWrittenEntry()
    {
        var sut = CreateSut();
        var payload = new MediaPayload(
            Url, null, null, [Video(Path.Combine(_downloads, "never-existed.mp4"))]);

        await sut.SaveAsync(payload, "YtDlpPlatformExtractor", CancellationToken.None);

        (await sut.TryGetAsync(Url, CancellationToken.None)).Should().BeNull();
        Directory.GetDirectories(CacheRoot).Should().BeEmpty();
    }

    [Fact]
    public async Task Prune_DeletesEntriesPastTheirLifetime_AndKeepsTheRest()
    {
        var sut = CreateSut();
        await sut.SaveAsync(
            new MediaPayload(Url, null, null, [Video(WriteDownload("old.mp4", "old"))]),
            "YtDlpPlatformExtractor",
            CancellationToken.None);

        _clock.Advance(TimeSpan.FromHours(23));
        var fresh = new Uri("https://tiktok.com/@user/video/999");
        await sut.SaveAsync(
            new MediaPayload(fresh, null, null, [Video(WriteDownload("new.mp4", "new"))]),
            "YtDlpPlatformExtractor",
            CancellationToken.None);

        _clock.Advance(TimeSpan.FromHours(2));
        var deleted = sut.Prune();

        deleted.Should().Be(1);
        (await sut.TryGetAsync(Url, CancellationToken.None)).Should().BeNull();
        (await sut.TryGetAsync(fresh, CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public void Prune_DeletesLeftoversWithNoEntryFile()
    {
        var sut = CreateSut();
        var orphan = Path.Combine(CacheRoot, "0123456789abcdef0123456789abcdef");
        Directory.CreateDirectory(orphan);
        File.WriteAllText(Path.Combine(orphan, "00.mp4"), "bytes");

        sut.Prune().Should().Be(1);

        // A crash between copying the media and writing entry.json leaves bytes nobody can serve.
        Directory.Exists(orphan).Should().BeFalse();
    }

    [Fact]
    public async Task Prune_OverTheSizeCap_EvictsTheOldestEntriesFirst()
    {
        // Cap of 1MB against two ~600KB entries: the pair doesn't fit, the newer one alone does.
        var sut = CreateSut(new MediaCacheOptions { Directory = CacheRoot, MaxTotalSizeMb = 1 });
        var big = new string('x', 600 * 1024);

        await sut.SaveAsync(
            new MediaPayload(Url, null, null, [Video(WriteDownload("old.mp4", big))]),
            "YtDlpPlatformExtractor",
            CancellationToken.None);

        _clock.Advance(TimeSpan.FromMinutes(5));
        var newer = new Uri("https://tiktok.com/@user/video/999");
        await sut.SaveAsync(
            new MediaPayload(newer, null, null, [Video(WriteDownload("new.mp4", big))]),
            "YtDlpPlatformExtractor",
            CancellationToken.None);

        // The second save prunes as it lands, so the eviction has already happened here.
        (await sut.TryGetAsync(Url, CancellationToken.None)).Should().BeNull();
        (await sut.TryGetAsync(newer, CancellationToken.None)).Should().NotBeNull();
    }

    private string CacheRoot => Path.Combine(_root, "cache");

    private FileSystemMediaCache CreateSut(MediaCacheOptions? options = null) =>
        new(Options.Create(options ?? new MediaCacheOptions { Directory = CacheRoot }),
            _clock,
            NullLogger<FileSystemMediaCache>.Instance);

    private static MediaItem Video(string path) =>
        new(path, MediaKind.Video, "video/mp4", null, 5);

    private string WriteDownload(string name, string content)
    {
        var path = Path.Combine(_downloads, name);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException) { /* best-effort temp cleanup */ }
    }
}
