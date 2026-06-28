using LeBot.Application.Ports;
using LeBot.Infrastructure.Configuration;
using LeBot.Infrastructure.Maintenance;
using LeBot.Infrastructure.MediaExtraction.InstagramEmbed;
using LeBot.Infrastructure.MediaExtraction.ThreadsEmbed;
using LeBot.Infrastructure.MediaExtraction.YtDlp;
using LeBot.Infrastructure.Releases;
using LeBot.Infrastructure.Telegram;
using LeBot.Infrastructure.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Telegram.Bot;

namespace LeBot.Infrastructure;

/// <summary>
/// Wires the Infrastructure adapters into the DI container.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructureServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<TelegramOptions>(configuration.GetSection(TelegramOptions.SectionName));
        services.Configure<YtDlpOptions>(configuration.GetSection(YtDlpOptions.SectionName));
        services.Configure<UpdateOptions>(configuration.GetSection(UpdateOptions.SectionName));

        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<ITelegramBotClient>(sp =>
        {
            var token = sp.GetRequiredService<IOptions<TelegramOptions>>().Value.BotToken;
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException(
                    "Telegram:BotToken is not configured. Set it via 'dotnet user-secrets set \"Telegram:BotToken\" \"<token>\" --project src/LeBot.Host' or the 'Telegram__BotToken' environment variable.");
            }

            return new TelegramBotClient(token);
        });

        services.AddHttpClient();

        services.AddSingleton<ITelegramMessenger, TelegramBotMessenger>();
        services.AddSingleton<IUrlExtractor, RegexUrlExtractor>();

        // Order matters: extractors are tried in the order they're registered. The embed scrapers
        // claim narrow URL families that yt-dlp can't (Instagram image carousels, Threads posts on
        // the new .com domain) and run first so a successful embed pulls the chain out before yt-dlp
        // wastes a process spawn on the same URL. YtDlpPlatformExtractor handles everything else and
        // also acts as a fallback when embeds return nothing useful.
        services.AddSingleton<IPlatformExtractor, InstagramEmbedExtractor>();
        services.AddSingleton<IPlatformExtractor, ThreadsEmbedExtractor>();
        services.AddSingleton<IPlatformExtractor, YtDlpPlatformExtractor>();

        services.AddSingleton<IReleaseSource, GitHubReleaseSource>();
        services.AddSingleton<IAppVersion, AssemblyAppVersion>();
        services.AddSingleton<IUpdateInstaller, UpdateInstaller>();

        services.AddHostedService<TelegramUpdateDispatcher>();
        services.AddHostedService<DownloadsCleanupService>();
        services.AddHostedService<YtDlpUpdateService>();
        services.AddHostedService<SelfUpdateService>();

        return services;
    }
}
