using LeBot.Application.Ports;
using LeBot.Application.Releases;
using LeBot.Domain.Common;
using LeBot.Infrastructure.Configuration;
using LeBot.Infrastructure.Maintenance;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LeBot.Infrastructure.Tests.Maintenance;

public class SelfUpdateServiceTests
{
    private static readonly ReleaseVersion Current = new(1, 2, 3);

    private readonly IReleaseSource _releaseSource = Substitute.For<IReleaseSource>();
    private readonly IUpdateInstaller _installer = Substitute.For<IUpdateInstaller>();
    private readonly IAppVersion _appVersion = Substitute.For<IAppVersion>();
    private readonly ITelegramMessenger _messenger = Substitute.For<ITelegramMessenger>();
    private readonly FakeLifetime _lifetime = new();

    public SelfUpdateServiceTests()
    {
        _appVersion.Current.Returns(Current);
    }

    [Fact]
    public async Task RunOnceAsync_UpToDate_DoesNothing()
    {
        GivenLatestRelease(new ReleaseVersion(1, 2, 3));
        var service = BuildService();

        var action = await service.RunOnceAsync(CancellationToken.None);

        action.Should().Be(UpdateAction.None);
        await _installer.DidNotReceiveWithAnyArgs().DownloadAndVerifyAsync(default!, default);
        await _messenger.DidNotReceiveWithAnyArgs().SendTextAsync(default, default!, default);
        _lifetime.StopRequested.Should().BeFalse();
    }

    [Fact]
    public async Task RunOnceAsync_NoReleases_ReturnsNone()
    {
        _releaseSource.GetLatestAsync(Arg.Any<CancellationToken>())
            .Returns(Result<ReleaseInfo, ReleaseSourceError>.Failure(ReleaseSourceError.NoReleases));
        var service = BuildService();

        var action = await service.RunOnceAsync(CancellationToken.None);

        action.Should().Be(UpdateAction.None);
        await _installer.DidNotReceiveWithAnyArgs().DownloadAndVerifyAsync(default!, default);
    }

    [Fact]
    public async Task RunOnceAsync_NewerInNotifyOnly_NotifiesWithoutInstalling()
    {
        GivenLatestRelease(new ReleaseVersion(2, 0, 0));
        var service = BuildService(UpdateMode.NotifyOnly, notifyChatId: 42);

        var action = await service.RunOnceAsync(CancellationToken.None);

        action.Should().Be(UpdateAction.Notify);
        await _messenger.Received(1).SendTextAsync(42, Arg.Is<string>(t => t.Contains("2.0.0")), Arg.Any<CancellationToken>());
        await _installer.DidNotReceiveWithAnyArgs().DownloadAndVerifyAsync(default!, default);
        _lifetime.StopRequested.Should().BeFalse();
    }

    [Fact]
    public async Task RunOnceAsync_NewerInApplyAndVerified_InstallsAndStops()
    {
        var release = GivenLatestRelease(new ReleaseVersion(2, 0, 0));
        _installer.DownloadAndVerifyAsync(release, Arg.Any<CancellationToken>())
            .Returns(Result<string, UpdateInstallError>.Success("staged.exe.new"));
        var service = BuildService(UpdateMode.Apply, notifyChatId: 7);

        var action = await service.RunOnceAsync(CancellationToken.None);

        action.Should().Be(UpdateAction.Apply);
        await _installer.Received(1).DownloadAndVerifyAsync(release, Arg.Any<CancellationToken>());
        _installer.Received(1).ApplyAndLaunchHelper("staged.exe.new", new ReleaseVersion(2, 0, 0));
        _lifetime.StopRequested.Should().BeTrue();
    }

    [Fact]
    public async Task RunOnceAsync_ShaMismatch_DoesNotSwapOrStop()
    {
        var release = GivenLatestRelease(new ReleaseVersion(2, 0, 0));
        _installer.DownloadAndVerifyAsync(release, Arg.Any<CancellationToken>())
            .Returns(Result<string, UpdateInstallError>.Failure(UpdateInstallError.ShaMismatch));
        var service = BuildService(UpdateMode.Apply, notifyChatId: 7);

        var action = await service.RunOnceAsync(CancellationToken.None);

        action.Should().Be(UpdateAction.None);
        _installer.DidNotReceiveWithAnyArgs().ApplyAndLaunchHelper(default!, default!);
        _lifetime.StopRequested.Should().BeFalse();
    }

    private ReleaseInfo GivenLatestRelease(ReleaseVersion version)
    {
        var release = new ReleaseInfo(
            version,
            new Uri("https://example.test/LeBot.Host.exe"),
            new string('a', 64),
            $"v{version}",
            null);
        _releaseSource.GetLatestAsync(Arg.Any<CancellationToken>())
            .Returns(Result<ReleaseInfo, ReleaseSourceError>.Success(release));
        return release;
    }

    private SelfUpdateService BuildService(UpdateMode mode = UpdateMode.Apply, long? notifyChatId = null)
    {
        var options = Options.Create(new UpdateOptions
        {
            Enabled = true,
            Mode = mode,
            NotifyChatId = notifyChatId,
        });
        return new SelfUpdateService(
            _releaseSource,
            _installer,
            _appVersion,
            _messenger,
            _lifetime,
            options,
            NullLogger<SelfUpdateService>.Instance);
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public bool StopRequested { get; private set; }

        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => StopRequested = true;
    }
}
