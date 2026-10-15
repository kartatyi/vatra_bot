using LeBot.Domain.Settings;
using Telegram.Bot.Types.ReplyMarkups;

namespace LeBot.Infrastructure.Telegram;

/// <summary>
/// The <c>/settings</c> message: its text, its inline keyboard, and the callback data the keyboard sends
/// back. One row per setting, one button per choice, the current choice ticked. Labels follow the language
/// of whoever opened or pressed it; the callback data never changes with the language. Pure, so the round
/// trip from button to <see cref="ChatSettings"/> is testable without Telegram.
/// </summary>
internal static class ChatSettingsKeyboard
{
    private const string Prefix = "cfg:";
    private const string VideoKey = "v";
    private const string PostKey = "p";
    private const string ButtonKey = "b";
    private const string ShrugKey = "s";
    private const string DigestKey = "d";
    private const string On = "On";
    private const string Off = "Off";
    private const string Tick = "✅ ";

    /// <summary>The message above the keyboard.</summary>
    public static string Text(BotLanguage lang) => lang.Pick(
        "⚙️ Налаштування цього чату\n\n" +
        "🎬 Відео — текст під TikTok, Reels, Shorts\n" +
        "📝 Пости — текст постів Threads, X, Reddit і фото\n" +
        "🔘 Кнопка — «" + RevealButton.Label + "» під репостом, якщо текст сховано чи скорочено\n" +
        "🤷 Знизування — 🤷 на лінк, який не вдалося завантажити\n" +
        "📬 Підсумок — щопонеділка вранці підсумок лінків за тиждень\n\n" +
        "Без тексту: лише медіа · Коротко: без хештегів, кілька рядків · Повністю: як у пості\n" +
        "Змінювати можуть лише адміни групи.",
        "⚙️ Settings for this chat\n\n" +
        "🎬 Video — the text under TikToks, Reels, Shorts\n" +
        "📝 Post — the text of Threads, X, Reddit and photo posts\n" +
        "🔘 Button — a «" + RevealButton.Label + "» button under a repost whose text was hidden or cut\n" +
        "🤷 Shrug — a 🤷 on a link the bot couldn't fetch\n" +
        "📬 Digest — a Monday-morning summary of last week's links\n\n" +
        "Off: media only · Brief: no hashtags, a few lines · Full: as posted\n" +
        "Only group admins can change these.");

    public static string AdminsOnly(BotLanguage lang) => lang.Pick(
        "Змінювати ці налаштування можуть лише адміни групи.",
        "Only group admins can change these settings.");

    public static string Saved(BotLanguage lang) => lang.Pick("Збережено ✓", "Saved ✓");

    /// <summary>The keyboard for <paramref name="settings"/>, with the active choice of each row ticked.</summary>
    public static InlineKeyboardMarkup Markup(ChatSettings settings, BotLanguage lang) => new(
    [
        [
            ModeButton(lang.Pick("🎬 Без тексту", "🎬 Off"), VideoKey, CaptionMode.Off, settings.VideoCaption),
            ModeButton(lang.Pick("🎬 Коротко", "🎬 Brief"), VideoKey, CaptionMode.Brief, settings.VideoCaption),
            ModeButton(lang.Pick("🎬 Повністю", "🎬 Full"), VideoKey, CaptionMode.Full, settings.VideoCaption),
        ],
        [
            ModeButton(lang.Pick("📝 Коротко", "📝 Brief"), PostKey, CaptionMode.Brief, settings.PostCaption),
            ModeButton(lang.Pick("📝 Повністю", "📝 Full"), PostKey, CaptionMode.Full, settings.PostCaption),
        ],
        [
            Button(lang.Pick("🔘 Кнопка: так", "🔘 Button on"), ButtonKey, On, settings.OfferFullText),
            Button(lang.Pick("🔘 Кнопка: ні", "🔘 Button off"), ButtonKey, Off, !settings.OfferFullText),
        ],
        [
            Button(lang.Pick("🤷 Знизування: так", "🤷 Shrug on"), ShrugKey, On, settings.ShrugOnFailure),
            Button(lang.Pick("🤷 Знизування: ні", "🤷 Shrug off"), ShrugKey, Off, !settings.ShrugOnFailure),
        ],
        [
            Button(lang.Pick("📬 Підсумок: так", "📬 Digest on"), DigestKey, On, settings.WeeklyDigest),
            Button(lang.Pick("📬 Підсумок: ні", "📬 Digest off"), DigestKey, Off, !settings.WeeklyDigest),
        ],
    ]);

    /// <summary>
    /// Applies the button behind <paramref name="callbackData"/> to <paramref name="current"/>. False for
    /// anything this keyboard didn't send — another feature's button, or a forged payload such as
    /// "post: off", which <see cref="ChatSettings"/> would refuse.
    /// </summary>
    public static bool TryApply(string? callbackData, ChatSettings current, out ChatSettings updated)
    {
        updated = current;
        if (callbackData is null || !callbackData.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var parts = callbackData[Prefix.Length..].Split(':');
        if (parts.Length != 2)
        {
            return false;
        }

        var (key, value) = (parts[0], parts[1]);
        if (key is ButtonKey or ShrugKey or DigestKey)
        {
            if (value is not (On or Off))
            {
                return false;
            }

            var on = value == On;
            updated = key switch
            {
                ButtonKey => current with { OfferFullText = on },
                ShrugKey => current with { ShrugOnFailure = on },
                _ => current with { WeeklyDigest = on },
            };
            return true;
        }

        // Only the exact names the keyboard emits — Enum.TryParse alone would also take "7" or " Full".
        if (!Enum.TryParse<CaptionMode>(value, ignoreCase: false, out var mode) || !Enum.IsDefined(mode) ||
            value != mode.ToString())
        {
            return false;
        }

        switch (key)
        {
            case VideoKey:
                updated = current with { VideoCaption = mode };
                return true;
            case PostKey when mode != CaptionMode.Off:
                updated = new ChatSettings(current.VideoCaption, mode, current.OfferFullText, current.ShrugOnFailure, current.WeeklyDigest);
                return true;
            default:
                return false;
        }
    }

    private static InlineKeyboardButton ModeButton(string label, string key, CaptionMode mode, CaptionMode active) =>
        Button(label, key, mode.ToString(), mode == active);

    private static InlineKeyboardButton Button(string label, string key, string value, bool active) =>
        InlineKeyboardButton.WithCallbackData(active ? Tick + label : label, $"{Prefix}{key}:{value}");
}
