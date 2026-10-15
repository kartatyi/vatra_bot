using LeBot.Application.Metrics;
using LeBot.Application.Telemetry;
using LeBot.Application.UseCases.AnswerInlineQuery;
using LeBot.Application.UseCases.HandleIncomingMessage;
using Microsoft.Extensions.DependencyInjection;

namespace LeBot.Application;

/// <summary>
/// Registration for the application-layer use-cases. Called from the composition root.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddSingleton<RepostMetrics>();
        services.AddSingleton<IRepostJournal, RepostJournal>();
        services.AddSingleton<PersonStats>();
        services.AddSingleton<HandleIncomingMessageHandler>();
        services.AddSingleton<InlinePostPreparer>();
        services.AddSingleton<AnswerInlineQueryHandler>();
        return services;
    }
}
