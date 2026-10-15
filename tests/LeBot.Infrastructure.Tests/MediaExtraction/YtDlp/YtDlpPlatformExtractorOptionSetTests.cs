using LeBot.Infrastructure.Configuration;
using LeBot.Infrastructure.MediaExtraction.YtDlp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LeBot.Infrastructure.Tests.MediaExtraction.YtDlp;

public class YtDlpPlatformExtractorOptionSetTests
{
    private static YtDlpPlatformExtractor CreateSut(string jsRuntimePath = "no-such-dir/deno.exe")
    {
        var options = Options.Create(new YtDlpOptions
        {
            DownloadDirectory = Path.GetTempPath(),
            JsRuntimePath = jsRuntimePath,
        });
        return new YtDlpPlatformExtractor(options, NullLogger<YtDlpPlatformExtractor>.Instance);
    }

    [Fact]
    public void BuildOptionSet_Always_RanksH264AboveH265()
    {
        // TikTok's h265 (bytevc1) streams claim acodec=aac but ship without an audio
        // track, so the format sort must steer "best" towards h264 — the variants
        // that actually carry sound. A format filter can't do this: [acodec!=none]
        // trusts the same lying metadata.
        var optionSet = CreateSut().BuildOptionSet();

        optionSet.FormatSort.Should().Be("vcodec:h264");
    }

    [Fact]
    public void BuildOptionSet_DenoPresent_PassesItAsJsRuntime()
    {
        var deno = Path.Combine(Path.GetTempPath(), $"deno-{Environment.ProcessId}.exe");
        File.WriteAllBytes(deno, []);

        try
        {
            var args = CreateSut(deno).BuildOptionSet().GetOptionFlags().ToList();

            args.Should().Contain($"--js-runtimes \"deno:{deno}\"");
        }
        finally
        {
            File.Delete(deno);
        }
    }

    [Fact]
    public void BuildOptionSet_DenoMissing_LeavesJsRuntimeUnset()
    {
        var args = CreateSut().BuildOptionSet().GetOptionFlags();

        args.Should().NotContain(flag => flag.StartsWith("--js-runtimes"));
    }
}
