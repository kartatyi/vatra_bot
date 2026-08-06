using Microsoft.Extensions.Configuration;

namespace LeBot.Infrastructure.Configuration;

/// <summary>
/// Rebases Serilog's relative file-sink paths onto an absolute directory next to the executable.
/// Serilog resolves a relative <c>path</c> against the current working directory, so a bare
/// <c>logs/lebot-.log</c> lands wherever the process happened to start — System32 for an elevated
/// installer, the repo root in dev, the install dir under Task Scheduler. Pinning it absolute means
/// the logs are always exactly where the operator expects: beside the binary.
/// </summary>
public static class LogPathResolver
{
    /// <summary>
    /// Returns config overrides (key → absolute path) for every <c>WriteTo</c> File sink whose path is
    /// relative. Feed the result to <c>AddInMemoryCollection</c> so it wins over the on-disk value.
    /// Sinks already configured with an absolute path are left untouched. The File sink is located by
    /// its <c>Name</c>, not a fixed array index, so reordering the sinks can't break this.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> ResolveAbsolutePaths(
        IConfiguration configuration,
        string baseDirectory)
    {
        var overrides = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var sink in configuration.GetSection("Serilog:WriteTo").GetChildren())
        {
            if (!string.Equals(sink["Name"], "File", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var configuredPath = sink["Args:path"];
            if (string.IsNullOrWhiteSpace(configuredPath) || Path.IsPathRooted(configuredPath))
            {
                continue;
            }

            var key = ConfigurationPath.Combine(sink.Path, "Args", "path");
            overrides[key] = Path.GetFullPath(Path.Combine(baseDirectory, configuredPath));
        }

        return overrides;
    }

    /// <summary>
    /// The absolute directory the rolling log files land in, derived from the first File sink and
    /// falling back to <c>&lt;baseDirectory&gt;/logs</c> when no File sink declares a path. Used by
    /// <c>--doctor</c> and the installer to test writability and tell the operator where to look.
    /// </summary>
    public static string ResolveLogDirectory(IConfiguration configuration, string baseDirectory)
    {
        foreach (var sink in configuration.GetSection("Serilog:WriteTo").GetChildren())
        {
            if (!string.Equals(sink["Name"], "File", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var configuredPath = sink["Args:path"];
            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                continue;
            }

            var absolutePath = Path.IsPathRooted(configuredPath)
                ? configuredPath
                : Path.GetFullPath(Path.Combine(baseDirectory, configuredPath));
            var directory = Path.GetDirectoryName(absolutePath);
            if (!string.IsNullOrEmpty(directory))
            {
                return directory;
            }
        }

        return Path.GetFullPath(Path.Combine(baseDirectory, "logs"));
    }
}
