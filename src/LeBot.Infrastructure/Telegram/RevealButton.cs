using LeBot.Domain.Media;
using Telegram.Bot.Types.ReplyMarkups;

namespace LeBot.Infrastructure.Telegram;

/// <summary>
/// The "show text" button under a repost whose caption the chat's settings dropped or cut. Pressing it
/// swaps the full text into that same message — the chat gets no extra message, and the button goes away.
/// </summary>
internal static class RevealButton
{
    /// <summary>Callback data the button sends; distinct from every <c>/settings</c> button's.</summary>
    public const string CallbackData = "reveal";

    public const string Label = "📝 Text";

    public const string Gone = "This text is no longer available.";

    /// <summary>The button for <paramref name="payload"/>, or <see langword="null"/> when it has nothing held back.</summary>
    public static InlineKeyboardMarkup? For(MediaPayload payload) =>
        payload.RevealableText is null
            ? null
            : new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData(Label, CallbackData));
}
