using LeBot.Infrastructure.Configuration;

namespace LeBot.Infrastructure.Tests.Configuration;

public class YtDlpOptionsTests
{
    [Fact]
    public void ResolvedDownloadDirectory_RelativeValue_RebasesOntoExecutableDirectory()
    {
        var options = new YtDlpOptions { DownloadDirectory = "downloads" };

        options.ResolvedDownloadDirectory
            .Should().Be(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "downloads")));
        Path.IsPathRooted(options.ResolvedDownloadDirectory).Should().BeTrue();
    }

    [Fact]
    public void ResolvedDownloadDirectory_AbsoluteValue_IsUnchanged()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "lebot-downloads");
        var options = new YtDlpOptions { DownloadDirectory = absolute };

        options.ResolvedDownloadDirectory.Should().Be(absolute);
    }
}
