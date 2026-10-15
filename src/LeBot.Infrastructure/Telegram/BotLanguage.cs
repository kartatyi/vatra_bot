namespace LeBot.Infrastructure.Telegram;

/// <summary>
/// The language the bot speaks to a person: English, unless their Telegram is set to Ukrainian. Copy that
/// isn't addressed to one person — the button under a repost, the weekly digest — is English too.
/// </summary>
internal enum BotLanguage
{
    En,
    Uk,
}

/// <summary>Picking the language, and the two helpers every piece of copy needs.</summary>
internal static class BotLanguages
{
    /// <summary>The language for a Telegram user's <c>language_code</c> (e.g. "uk", "en-GB", "pl", null).</summary>
    public static BotLanguage From(string? languageCode) =>
        languageCode is not null && languageCode.StartsWith("uk", StringComparison.OrdinalIgnoreCase)
            ? BotLanguage.Uk
            : BotLanguage.En;

    /// <summary>The copy for <paramref name="language"/>.</summary>
    public static string Pick(this BotLanguage language, string uk, string en) => language == BotLanguage.Uk ? uk : en;

    /// <summary>
    /// "<paramref name="n"/> thing(s)" with the right form: Ukrainian has three — 1 лінк, 2 лінки, 5 лінків
    /// (and 11–14 take the third, 21 the first again) — English two.
    /// </summary>
    public static string Count(this BotLanguage language, int n, string ukOne, string ukFew, string ukMany, string enOne, string enMany)
    {
        if (language == BotLanguage.En)
        {
            return $"{n} {(n == 1 ? enOne : enMany)}";
        }

        var lastTwo = Math.Abs(n) % 100;
        var last = lastTwo % 10;
        var form = lastTwo is >= 11 and <= 14 ? ukMany
            : last == 1 ? ukOne
            : last is >= 2 and <= 4 ? ukFew
            : ukMany;
        return $"{n} {form}";
    }
}
