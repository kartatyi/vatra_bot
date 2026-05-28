using LeBot.Application;
using LeBot.Application.Ports;
using LeBot.Application.UseCases.HandleIncomingMessage;
using LeBot.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LeBot.Infrastructure.Tests;

/// <summary>
/// CLAUDE.md §5 requires a Host-level smoke test that the DI graph resolves.
/// The Host project itself has no test project — this lives in Infrastructure.Tests
/// because every concrete service the Host wires up comes from this layer.
/// </summary>
public class HostCompositionSmokeTests
{
    [Fact]
    public void DiGraph_ResolvesEveryPort()
    {
        var builder = Host.CreateApplicationBuilder([]);

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Telegram:BotToken"] = "0123456789:placeholder-token-for-resolution-only",
            ["YtDlp:BinaryPath"] = "yt-dlp",
            ["YtDlp:DownloadDirectory"] = Path.Combine(Path.GetTempPath(), "lebot-smoke-test"),
            ["YtDlp:MaxFileSizeMb"] = "50",
        });

        builder.Services.AddApplicationServices();
        builder.Services.AddInfrastructureServices(builder.Configuration);

        using var host = builder.Build();

        // The application use-case resolves only when every port behind it does too,
        // so this single call exercises the full Domain → Application → Infrastructure chain.
        var handler = host.Services.GetRequiredService<HandleIncomingMessageHandler>();
        handler.Should().NotBeNull();

        // Spot-check the ports directly so a registration drift on any one of them
        // produces a focused failure name instead of a confusing handler-construction error.
        host.Services.GetRequiredService<IUrlExtractor>().Should().NotBeNull();
        host.Services.GetRequiredService<ITelegramMessenger>().Should().NotBeNull();
        host.Services.GetServices<IPlatformExtractor>().Should().HaveCountGreaterThanOrEqualTo(2,
            "the dispatcher relies on both the embed scraper and the yt-dlp extractor being registered");
    }

    [Fact]
    public void DiGraph_ThrowsHelpfullyWhenBotTokenIsMissing()
    {
        var builder = Host.CreateApplicationBuilder([]);

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["YtDlp:BinaryPath"] = "yt-dlp",
            ["YtDlp:DownloadDirectory"] = Path.Combine(Path.GetTempPath(), "lebot-smoke-test"),
        });

        builder.Services.AddApplicationServices();
        builder.Services.AddInfrastructureServices(builder.Configuration);

        using var host = builder.Build();

        // Resolving the bot client triggers our token-validation lambda. The exception
        // message must point a fresh operator at user-secrets / env vars — that's the
        // contract the README's quick-start depends on.
        var act = () => host.Services.GetRequiredService<global::Telegram.Bot.ITelegramBotClient>();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Telegram:BotToken*user-secrets*");
    }
}
