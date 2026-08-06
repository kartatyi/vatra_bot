using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;

namespace LeBot.Infrastructure.Configuration;

/// <summary>
/// Loads the canonical <c>appsettings.json</c> that ships embedded in the binary as the base
/// configuration layer, so the Serilog / YtDlp / Update defaults can never be "forgotten" — not even
/// when someone copies the lone <c>.exe</c> to a server and leaves every JSON file behind. On-disk
/// <c>appsettings.json</c> / <c>appsettings.Local.json</c> still layer on top as optional overrides.
/// </summary>
/// <remarks>
/// The embedded resource is the Host's <c>appsettings.json</c>, linked into this assembly at build time
/// (see LeBot.Infrastructure.csproj) so the embedded defaults and the on-disk file can never drift. It
/// lives in Infrastructure rather than Host purely so it sits in a layer the test suite already covers.
/// <para>
/// The JSON is flattened to key/value pairs and added via an in-memory source rather than
/// <c>AddJsonStream</c>: a stream is read-once, but <see cref="ConfigurationManager"/> rebuilds every
/// provider whenever a later source is added, which would replay an already-consumed stream and lose
/// the defaults. An in-memory source is re-readable and survives those rebuilds.
/// </para>
/// </remarks>
public static class EmbeddedAppConfiguration
{
    /// <summary>Manifest resource name of the embedded defaults (see LeBot.Infrastructure.csproj).</summary>
    public const string ResourceName = "LeBot.Infrastructure.appsettings.defaults.json";

    /// <summary>Inserts the embedded defaults as the lowest-precedence (base) configuration layer.</summary>
    public static IConfigurationBuilder AddEmbeddedDefaults(this IConfigurationBuilder builder)
    {
        builder.Sources.Insert(0, new MemoryConfigurationSource { InitialData = ReadDefaultsAsKeyValues() });
        return builder;
    }

    /// <summary>The raw embedded JSON, used by <c>--install</c> to drop an editable copy next to the exe.</summary>
    public static string ReadDefaults()
    {
        using var stream = OpenDefaults();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Builds a configuration containing only the embedded defaults — handy for tests and probes.</summary>
    public static IConfiguration BuildDefaults()
    {
        var builder = new ConfigurationBuilder();
        builder.AddEmbeddedDefaults();
        return builder.Build();
    }

    private static List<KeyValuePair<string, string?>> ReadDefaultsAsKeyValues()
    {
        using var stream = OpenDefaults();
        var parsed = new ConfigurationBuilder().AddJsonStream(stream).Build();
        try
        {
            // AsEnumerable() also yields the intermediate parent keys with null values; keep only leaves.
            return parsed.AsEnumerable()
                .Where(pair => pair.Value is not null)
                .ToList();
        }
        finally
        {
            (parsed as IDisposable)?.Dispose();
        }
    }

    private static Stream OpenDefaults() =>
        typeof(EmbeddedAppConfiguration).Assembly.GetManifestResourceStream(ResourceName)
        ?? throw new InvalidOperationException(
            $"Embedded default configuration '{ResourceName}' is missing from the assembly. "
            + "Confirm appsettings.json is linked as an <EmbeddedResource> in LeBot.Infrastructure.csproj.");
}
