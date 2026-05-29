namespace LeBot.Infrastructure.Configuration;

/// <summary>
/// Resolves a configured tool path (yt-dlp, ffmpeg) against the current working directory first
/// — which works when the bot is launched from the repo root or any folder containing the tool —
/// and then by walking up from the assembly base directory until the file is found. The latter
/// covers <c>dotnet run --project src/LeBot.Host</c> in development, where the CWD is the project
/// directory several levels below the repo root. Absolute paths pass through unchanged.
/// </summary>
internal static class ExecutablePathResolver
{
    public static string Resolve(string configured)
    {
        if (string.IsNullOrEmpty(configured) || Path.IsPathRooted(configured))
        {
            return configured;
        }

        var cwdRelative = Path.GetFullPath(configured);
        if (File.Exists(cwdRelative))
        {
            return cwdRelative;
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, configured);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return configured;
    }
}
