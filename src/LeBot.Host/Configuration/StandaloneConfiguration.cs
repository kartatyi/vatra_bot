using LeBot.Infrastructure.Configuration;

namespace LeBot.Host.Configuration;

/// <summary>
/// Assembles configuration for the one-shot verbs (<c>--doctor</c>, <c>--install</c>) that run before
/// any host is built. Mirrors the precedence the running host uses in <c>Program.cs</c> — embedded
/// defaults at the base, then on-disk <c>appsettings*.json</c>, user-secrets, environment variables,
/// and finally <c>appsettings.Local.json</c> — and roots file lookups at the executable's own
/// directory so a verb sees the same config the service will.
/// </summary>
internal static class StandaloneConfiguration
{
    public static IConfiguration ForExecutable(string baseDirectory)
    {
        var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? "Production";

        var builder = new ConfigurationBuilder().SetBasePath(baseDirectory);
        builder.AddEmbeddedDefaults();
        builder.AddJsonFile("appsettings.json", optional: true);
        builder.AddJsonFile($"appsettings.{environment}.json", optional: true);
        builder.AddUserSecrets(typeof(StandaloneConfiguration).Assembly, optional: true);
        builder.AddEnvironmentVariables();
        builder.AddJsonFile("appsettings.Local.json", optional: true);
        return builder.Build();
    }
}
