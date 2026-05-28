using LeBot.Application.Ports;
using LeBot.Infrastructure.Configuration;
using LeBot.Infrastructure.MediaExtraction.YtDlp;
using LeBot.Infrastructure.Telegram;
using LeBot.Infrastructure.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

        services.AddSingleton<ITelegramMessenger, TelegramBotMessenger>();
        services.AddSingleton<IUrlExtractor, RegexUrlExtractor>();
        services.AddSingleton<IPlatformExtractor, YtDlpPlatformExtractor>();

        services.AddHostedService<TelegramUpdateDispatcher>();

        return services;
    }
}
