using System.Reflection;

namespace LeBot.Dashboard;

/// <summary>
/// Serves the embedded single-page UI. Read once from the assembly manifest at startup and cached.
/// </summary>
internal static class DashboardPage
{
    public static string Html { get; } = Load();

    private static string Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("dashboard.html")
            ?? throw new InvalidOperationException("Embedded resource 'dashboard.html' is missing from the build.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
