using System.Text;
using LeBot.Domain.Common;
using Telegram.Bot.Types;

namespace LeBot.Infrastructure.Telegram;

/// <summary>
/// Builds the <c>/start</c> and <c>/help</c> reply from the bot's own Telegram profile — its description
/// and command menu, as the operator set them through BotFather or the Bot API. The profile is the single
/// source of the copy: the deployment writes it in its own language, and the repo carries only the
/// fallback for a bot whose profile is still blank.
/// </summary>
internal static class HelpReportFormatter
{
    internal const string FallbackDescription =
        "I repost the video or photo behind any link posted in this chat — TikTok, Instagram, " +
        "YouTube Shorts, Threads, X, Reddit and ~1800 more sites. Just drop a link.";

    private static readonly ReleaseVersion DevVersion = new(0, 0, 0);

    /// <summary>
    /// The profile <paramref name="description"/> (or <see cref="FallbackDescription"/> when blank), then
    /// one line per menu command, then the running version — left off for an unstamped dev build, whose
    /// <c>0.0.0</c> would only mislead.
    /// </summary>
    public static string Help(string? description, IReadOnlyList<BotCommand> commands, ReleaseVersion version)
    {
        var sb = new StringBuilder(string.IsNullOrWhiteSpace(description) ? FallbackDescription : description.Trim());

        if (commands.Count > 0)
        {
            sb.Append("\n\n");
            foreach (var command in commands)
            {
                sb.Append('/').Append(command.Command).Append(" — ").Append(command.Description).Append('\n');
            }

            sb.Length--; // drop the last command's trailing newline
        }

        if (version != DevVersion)
        {
            sb.Append("\n\nv").Append(version);
        }

        return sb.ToString();
    }
}
