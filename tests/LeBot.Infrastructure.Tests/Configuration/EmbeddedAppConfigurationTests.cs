using System.Text.Json;
using LeBot.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;

namespace LeBot.Infrastructure.Tests.Configuration;

public class EmbeddedAppConfigurationTests
{
    [Fact]
    public void BuildDefaults_NoFilesOnDisk_YieldsConsoleAndFileSerilogSinks()
    {
        var configuration = EmbeddedAppConfiguration.BuildDefaults();

        var sinks = configuration.GetSection("Serilog:WriteTo").GetChildren().ToList();
        sinks.Should().Contain(sink => sink["Name"] == "Console");
        sinks.Should().Contain(sink => sink["Name"] == "File",
            "a lone exe with no appsettings.json must still log to a file");
    }

    [Fact]
    public void BuildDefaults_CarriesYtDlpAndUpdateDefaults()
    {
        var configuration = EmbeddedAppConfiguration.BuildDefaults();

        configuration.GetValue<int>("YtDlp:MaxFileSizeMb").Should().Be(50);
        configuration.GetValue<bool>("Update:Enabled").Should().BeTrue();
    }

    [Fact]
    public void AddEmbeddedDefaults_SitsBelowLaterSources()
    {
        var configuration = new ConfigurationBuilder()
            .AddEmbeddedDefaults()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["YtDlp:MaxFileSizeMb"] = "999" })
            .Build();

        configuration.GetValue<int>("YtDlp:MaxFileSizeMb").Should().Be(999, "on-disk / later sources override the embedded base");
    }

    [Fact]
    public void AddEmbeddedDefaults_SurvivesAddingMoreSourcesAfterwards()
    {
        // Regression guard: a read-once stream source would lose its data when a later AddJsonFile makes
        // ConfigurationManager rebuild every provider. The in-memory source must keep serving the defaults.
        var manager = new ConfigurationManager();
        ((IConfigurationBuilder)manager).AddEmbeddedDefaults();
        manager.AddInMemoryCollection(new Dictionary<string, string?> { ["Extra:Key"] = "value" });

        manager.GetValue<int>("YtDlp:MaxFileSizeMb").Should().Be(50);
        manager["Extra:Key"].Should().Be("value");
    }

    [Fact]
    public void ReadDefaults_ReturnsParseableJsonWithSerilogSection()
    {
        var json = EmbeddedAppConfiguration.ReadDefaults();

        using var document = JsonDocument.Parse(json);
        document.RootElement.TryGetProperty("Serilog", out _).Should().BeTrue();
    }
}
