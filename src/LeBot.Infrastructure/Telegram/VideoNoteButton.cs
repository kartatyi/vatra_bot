using LeBot.Domain.Media;
using Telegram.Bot.Types.ReplyMarkups;

namespace LeBot.Infrastructure.Telegram;

/// <summary>
/// The button under a round video. A round video can't carry a caption, so this is where its title goes —
/// "🔴 &lt;title&gt;" — and pressing it opens the stream the clip was cut from.
/// </summary>
internal static class VideoNoteButton
{
    /// <summary>Long titles are cut here: a button shows one line, and the clients clip it well before this.</summary>
    internal const int MaxTitleLength = 60;

    /// <summary>Shown when the platform gave no title. The label is the whole chat's, so it is in the default language.</summary>
    internal const string Untitled = "Live";

    private const string LiveMark = "🔴";

    public static InlineKeyboardMarkup For(MediaPayload payload)
    {
        var title = string.IsNullOrWhiteSpace(payload.Title) ? Untitled : payload.Title.ReplaceLineEndings(" ").Trim();

        // Streamers often open the title with the same red dot; one is enough.
        if (title.StartsWith(LiveMark, StringComparison.Ordinal))
        {
            title = title[LiveMark.Length..].TrimStart();
        }

        var label = $"{LiveMark} {TelegramBotMessenger.Fit(title, MaxTitleLength)}";
        return new InlineKeyboardMarkup(InlineKeyboardButton.WithUrl(label, payload.SourceUrl.AbsoluteUri));
    }
}
