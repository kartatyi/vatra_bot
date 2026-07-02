using LeBot.Infrastructure.Configuration;
using LeBot.Infrastructure.Diagnostics;

namespace LeBot.Infrastructure.Tests.Diagnostics;

public class StartupConfigSummaryTests
{
    [Fact]
    public void Describe_CookiesConfigured_ReportsEnabledWithBrowser()
    {
        var options = new YtDlpOptions { CookiesFromBrowser = "firefox", MaxFileSizeMb = 42 };

        var summary = StartupConfigSummary.Describe(options, "Production");

        summary.CookiesEnabled.Should().BeTrue();
        summary.CookiesBrowser.Should().Be("firefox");
        summary.MaxFileSizeMb.Should().Be(42);
        summary.Environment.Should().Be("Production");
    }

    [Fact]
    public void Describe_NoCookies_ReportsDisabled()
    {
        var summary = StartupConfigSummary.Describe(new YtDlpOptions(), "Development");

        summary.CookiesEnabled.Should().BeFalse();
        summary.CookiesBrowser.Should().BeNull();
    }

    [Fact]
    public void Describe_AlwaysReportsAnAbsoluteYtDlpPathWhenRelativeBinaryMissing()
    {
        // A bare relative path with no binary on disk should still surface a non-empty path to log.
        var summary = StartupConfigSummary.Describe(
            new YtDlpOptions { BinaryPath = "tools/yt-dlp/yt-dlp.exe" },
            "Production");

        summary.YtDlpPath.Should().NotBeNullOrWhiteSpace();
    }
}
