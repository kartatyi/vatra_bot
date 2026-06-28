using LeBot.Infrastructure.Configuration;
using LeBot.Infrastructure.Diagnostics;

namespace LeBot.Infrastructure.Tests.Diagnostics;

public class YtDlpProbeTests
{
    [Fact]
    public async Task CheckAsync_BinaryMissing_FailsWithActionableHint()
    {
        var options = new YtDlpOptions
        {
            BinaryPath = Path.Combine(Path.GetTempPath(), $"no-such-yt-dlp-{Guid.NewGuid():N}.exe"),
        };

        var check = await YtDlpProbe.CheckAsync(options, CancellationToken.None);

        check.Status.Should().Be(DoctorStatus.Fail);
        check.Name.Should().Be("yt-dlp");
        check.Detail.Should().Contain("not found");
    }
}
