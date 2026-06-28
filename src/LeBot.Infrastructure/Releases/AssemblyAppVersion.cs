using System.Reflection;
using LeBot.Application.Ports;
using LeBot.Domain.Common;
using Microsoft.Extensions.Logging;

namespace LeBot.Infrastructure.Releases;

/// <summary>
/// Reads the running build's version from its <see cref="AssemblyInformationalVersionAttribute"/>
/// (stamped by the release workflow from the git tag), falling back to the assembly version. An
/// unstamped dev build, or any parse failure, reports <c>0.0.0</c> so the updater treats it as older
/// than every real release. Never throws — the host must construct even on a malformed version.
/// </summary>
public sealed class AssemblyAppVersion : IAppVersion
{
    public ReleaseVersion Current { get; }

    public AssemblyAppVersion(ILogger<AssemblyAppVersion> logger)
    {
        Current = Resolve(logger);
    }

    private static ReleaseVersion Resolve(ILogger<AssemblyAppVersion> logger)
    {
        var fallback = new ReleaseVersion(0, 0, 0);
        var assembly = Assembly.GetEntryAssembly();
        if (assembly is null)
        {
            logger.LogWarning("No entry assembly available; defaulting app version to {Version}", fallback);
            return fallback;
        }

        var raw = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString();

        if (raw is not null)
        {
            var plus = raw.IndexOf('+', StringComparison.Ordinal);
            if (plus >= 0)
            {
                raw = raw[..plus];
            }
        }

        return ReleaseVersion.Parse(raw).Match(
            version => version,
            error =>
            {
                logger.LogWarning(
                    "Could not parse own version from {Raw} ({Error}); defaulting to {Version}",
                    raw ?? "<null>", error, fallback);
                return fallback;
            });
    }
}
