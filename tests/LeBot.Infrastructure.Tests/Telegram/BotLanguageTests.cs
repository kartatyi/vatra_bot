using LeBot.Application.Telemetry;
using LeBot.Infrastructure.Telegram;

namespace LeBot.Infrastructure.Tests.Telegram;

/// <summary>The language choice and the Ukrainian copy that differs most from the English.</summary>
public class BotLanguageTests
{
    [Theory]
    [InlineData("uk", "Uk")]
    [InlineData("uk-UA", "Uk")]
    [InlineData("en", "En")]
    [InlineData("pl", "En")]
    [InlineData(null, "En")]
    public void From_UkrainianInterfaceGetsUkrainian_EveryoneElseEnglish(string? code, string expected)
    {
        BotLanguages.From(code).ToString().Should().Be(expected);
    }

    [Theory]
    [InlineData(1, "1 лінк")]
    [InlineData(2, "2 лінки")]
    [InlineData(5, "5 лінків")]
    [InlineData(11, "11 лінків")]
    [InlineData(21, "21 лінк")]
    [InlineData(24, "24 лінки")]
    public void Count_Ukrainian_UsesAllThreeForms(int n, string expected)
    {
        BotLanguage.Uk.Count(n, "лінк", "лінки", "лінків", "link", "links").Should().Be(expected);
    }

    [Fact]
    public void FormatMe_Ukrainian()
    {
        var text = LeaderboardFormatter.FormatMe("Петро", new PersonalStats(22, 1, 5, "TikTok", 21, 9, 3, 1), allTime: false, BotLanguage.Uk);

        text.Should().Be(
            "📇 Петро у цьому чаті — останні 30 днів\n\n" +
            "🔗 22 лінки · #1 з 5\n" +
            "📱 Здебільшого TikTok (21)\n" +
            "🕐 Найактивніше близько 09:00\n" +
            "⚡ 3 лінки, які інші потім кинули знову · 🥱 1 повтор\n\n" +
            "/me all — за весь час");
    }

    [Fact]
    public void SettingsKeyboard_Ukrainian_LabelsChangeButCallbacksDont()
    {
        var uk = ChatSettingsKeyboard.Markup(LeBot.Domain.Settings.ChatSettings.Default, BotLanguage.Uk);
        var en = ChatSettingsKeyboard.Markup(LeBot.Domain.Settings.ChatSettings.Default, BotLanguage.En);

        uk.InlineKeyboard.First().Select(b => b.Text).Should().Equal("✅ 🎬 Без тексту", "🎬 Коротко", "🎬 Повністю");
        uk.InlineKeyboard.SelectMany(r => r).Select(b => b.CallbackData)
            .Should().Equal(en.InlineKeyboard.SelectMany(r => r).Select(b => b.CallbackData));
    }

    [Fact]
    public void Digest_Ukrainian_NamesTheMonthsInTheGenitive()
    {
        var digest = new LeBot.Application.Digest.WeeklyDigest(new DateOnly(2026, 9, 28), new DateOnly(2026, 10, 4), 54, 0, "TikTok", 31, []);

        DigestFormatter.Format(digest, [], BotLanguage.Uk).Should().StartWith(
            "📬 Тиждень у лінках · 28 вересня – 4 жовтня\n\n🔗 Переслано: 54 лінки (як і минулого тижня)");
    }
}
