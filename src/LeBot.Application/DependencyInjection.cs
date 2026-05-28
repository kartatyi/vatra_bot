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
        services.AddSingleton<HandleIncomingMessageHandler>();
        return services;
    }
}
