using LeBot.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;

namespace LeBot.Infrastructure.Tests.Configuration;

public class LogPathResolverTests
{
    private static readonly string BaseDirectory = Path.Combine(Path.GetTempPath(), "lebot-logbase");

    private const string PathKey = "Serilog:WriteTo:1:Args:path";

    private static IConfiguration ConfigWithFileSink(string path) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Serilog:WriteTo:0:Name"] = "Console",
                ["Serilog:WriteTo:1:Name"] = "File",
                ["Serilog:WriteTo:1:Args:path"] = path,
            })
            .Build();

    [Fact]
    public void ResolveAbsolutePaths_RelativeFileSink_RebasesUnderBaseDirectory()
    {
        var configuration = ConfigWithFileSink(Path.Combine("logs", "lebot-.log"));

        var overrides = LogPathResolver.ResolveAbsolutePaths(configuration, BaseDirectory);

        overrides.Should().ContainKey(PathKey);
        overrides[PathKey].Should().Be(Path.GetFullPath(Path.Combine(BaseDirectory, "logs", "lebot-.log")));
        Path.IsPathRooted(overrides[PathKey]).Should().BeTrue();
    }

    [Fact]
    public void ResolveAbsolutePaths_AbsoluteFileSink_IsLeftUntouched()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "elsewhere", "lebot-.log");
        var configuration = ConfigWithFileSink(absolute);

        var overrides = LogPathResolver.ResolveAbsolutePaths(configuration, BaseDirectory);

        overrides.Should().BeEmpty("an operator-set absolute path is honoured as-is");
    }

    [Fact]
    public void ResolveAbsolutePaths_OnlyConsoleSink_ReturnsEmpty()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Serilog:WriteTo:0:Name"] = "Console" })
            .Build();

        var overrides = LogPathResolver.ResolveAbsolutePaths(configuration, BaseDirectory);

        overrides.Should().BeEmpty();
    }

    [Fact]
    public void ResolveLogDirectory_RelativeFileSink_ReturnsAbsoluteDirectoryUnderBase()
    {
        var configuration = ConfigWithFileSink(Path.Combine("logs", "lebot-.log"));

        var directory = LogPathResolver.ResolveLogDirectory(configuration, BaseDirectory);

        directory.Should().Be(Path.GetFullPath(Path.Combine(BaseDirectory, "logs")));
    }

    [Fact]
    public void ResolveLogDirectory_AbsoluteFileSink_ReturnsItsOwnDirectory()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "elsewhere", "lebot-.log");
        var configuration = ConfigWithFileSink(absolute);

        var directory = LogPathResolver.ResolveLogDirectory(configuration, BaseDirectory);

        directory.Should().Be(Path.GetDirectoryName(absolute));
    }

    [Fact]
    public void ResolveLogDirectory_NoFileSink_FallsBackToBaseLogsFolder()
    {
        var configuration = new ConfigurationBuilder().Build();

        var directory = LogPathResolver.ResolveLogDirectory(configuration, BaseDirectory);

        directory.Should().Be(Path.GetFullPath(Path.Combine(BaseDirectory, "logs")));
    }
}
