using LeBot.Infrastructure.Configuration;
using LeBot.Infrastructure.Maintenance;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LeBot.Infrastructure.Tests.Maintenance;

public sealed class DownloadsCleanupServiceTests : IDisposable
{
    // MaxFileAge is 1h, so files written before 11:00 are stale at this instant.
    private static readonly DateTimeOffset Now = new(2026, 6, 28, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "lebot-cleanup-tests", Guid.NewGuid().ToString("N"));

    public DownloadsCleanupServiceTests() => Directory.CreateDirectory(_dir);

    [Theory]
    [InlineData(0, true)]    // just written — well within the window
    [InlineData(30, true)]   // still fresh
    [InlineData(90, false)]  // older than MaxFileAge — swept
    public void Sweep_DeletesFilesOnceTheyAgePastTheCutoff(int fileAgeMinutes, bool shouldSurvive)
    {
        var file = Path.Combine(_dir, "clip.mp4");
        File.WriteAllText(file, "payload");
        File.SetLastWriteTimeUtc(file, Now.UtcDateTime.AddMinutes(-fileAgeMinutes));

        CreateService(Now).Sweep();

        File.Exists(file).Should().Be(shouldSurvive);
    }

    [Fact]
    public void Sweep_LeavesFreshFiles_WhileDeletingStaleOnesInTheSamePass()
    {
        var stale = Path.Combine(_dir, "stale.mp4");
        var fresh = Path.Combine(_dir, "fresh.mp4");
        File.WriteAllText(stale, "old");
        File.WriteAllText(fresh, "new");
        File.SetLastWriteTimeUtc(stale, Now.UtcDateTime.AddHours(-2));
        File.SetLastWriteTimeUtc(fresh, Now.UtcDateTime.AddMinutes(-5));

        CreateService(Now).Sweep();

        File.Exists(stale).Should().BeFalse();
        File.Exists(fresh).Should().BeTrue();
    }

    [Fact]
    public void Sweep_CutoffTracksTheInjectedClock()
    {
        var file = Path.Combine(_dir, "clip.mp4");
        File.WriteAllText(file, "payload");
        File.SetLastWriteTimeUtc(file, Now.UtcDateTime.AddMinutes(-30));

        // At the original instant the file is fresh and survives; ninety minutes later the
        // same file is past the cutoff. Only the injected clock differs between the two sweeps.
        CreateService(Now).Sweep();
        File.Exists(file).Should().BeTrue();

        CreateService(Now.AddMinutes(90)).Sweep();
        File.Exists(file).Should().BeFalse();
    }

    private DownloadsCleanupService CreateService(DateTimeOffset now) =>
        new(Options.Create(new YtDlpOptions { DownloadDirectory = _dir }),
            new FakeTimeProvider(now),
            NullLogger<DownloadsCleanupService>.Instance);

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
