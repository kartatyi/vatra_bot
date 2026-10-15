using LeBot.Domain.Settings;
using LeBot.Infrastructure.Telegram;

namespace LeBot.Infrastructure.Tests.Telegram;

public class ChatSettingsKeyboardTests
{
    private static readonly string[][] TickedForBriefFullOn =
    [
        ["🎬 Off", "✅ 🎬 Brief", "🎬 Full"],
        ["📝 Brief", "✅ 📝 Full"],
        ["✅ 🔘 Button on", "🔘 Button off"],
        ["🤷 Shrug on", "✅ 🤷 Shrug off"],
        ["📬 Digest on", "✅ 📬 Digest off"],
    ];

    [Fact]
    public void Markup_TicksTheActiveChoiceOfEachRow()
    {
        var markup = ChatSettingsKeyboard.Markup(new ChatSettings(CaptionMode.Brief, CaptionMode.Full));

        var labels = markup.InlineKeyboard.Select(row => row.Select(b => b.Text).ToArray()).ToArray();
        labels.Should().BeEquivalentTo(TickedForBriefFullOn, options => options.WithStrictOrdering());
    }

    [Fact]
    public void Markup_EveryButtonRoundTripsThroughTryApply()
    {
        var markup = ChatSettingsKeyboard.Markup(ChatSettings.Default);

        foreach (var button in markup.InlineKeyboard.SelectMany(row => row))
        {
            ChatSettingsKeyboard.TryApply(button.CallbackData, ChatSettings.Default, out _)
                .Should().BeTrue(button.CallbackData);
        }
    }

    [Theory]
    [InlineData("cfg:v:Full", CaptionMode.Full, CaptionMode.Full, true)]
    [InlineData("cfg:v:Brief", CaptionMode.Brief, CaptionMode.Full, true)]
    [InlineData("cfg:p:Brief", CaptionMode.Off, CaptionMode.Brief, true)]
    [InlineData("cfg:b:Off", CaptionMode.Off, CaptionMode.Full, false)]
    public void TryApply_KnownButton_ChangesOnlyItsRow(string data, CaptionMode video, CaptionMode post, bool offer)
    {
        var applied = ChatSettingsKeyboard.TryApply(data, ChatSettings.Default, out var updated);

        applied.Should().BeTrue();
        updated.Should().Be(new ChatSettings(video, post, offer));
    }

    [Theory]
    [InlineData("cfg:s:On", true)]
    [InlineData("cfg:s:Off", false)]
    public void TryApply_ShrugRow_TogglesOnlyTheShrug(string data, bool shrug)
    {
        var applied = ChatSettingsKeyboard.TryApply(data, ChatSettings.Default, out var updated);

        applied.Should().BeTrue();
        updated.Should().Be(ChatSettings.Default with { ShrugOnFailure = shrug });
    }

    [Fact]
    public void TryApply_DigestRow_TurnsTheDigestOn()
    {
        ChatSettingsKeyboard.TryApply("cfg:d:On", ChatSettings.Default, out var updated).Should().BeTrue();

        updated.Should().Be(ChatSettings.Default with { WeeklyDigest = true });
    }

    [Fact]
    public void TryApply_PostRow_KeepsTheDigestSetting()
    {
        ChatSettingsKeyboard.TryApply("cfg:p:Brief", ChatSettings.Default with { WeeklyDigest = true }, out var updated);

        updated.WeeklyDigest.Should().BeTrue();
    }

    [Fact]
    public void TryApply_PostRow_KeepsTheShrugSetting()
    {
        var current = ChatSettings.Default with { ShrugOnFailure = true };

        ChatSettingsKeyboard.TryApply("cfg:p:Brief", current, out var updated);

        updated.ShrugOnFailure.Should().BeTrue();
    }

    [Fact]
    public void TryApply_PostRow_KeepsTheButtonSetting()
    {
        var current = new ChatSettings(CaptionMode.Off, CaptionMode.Full, OfferFullText: false);

        ChatSettingsKeyboard.TryApply("cfg:p:Brief", current, out var updated);

        updated.OfferFullText.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("reveal")]
    [InlineData("cfg:p:Off")]
    [InlineData("cfg:x:Full")]
    [InlineData("cfg:v:Loud")]
    [InlineData("cfg:v:7")]
    [InlineData("cfg:v:full")]
    [InlineData("cfg:b:Maybe")]
    [InlineData("cfg:s:Maybe")]
    [InlineData("cfg:v:Full:extra")]
    public void TryApply_UnknownOrForgedData_IsRejected(string? data)
    {
        var applied = ChatSettingsKeyboard.TryApply(data, ChatSettings.Default, out var updated);

        applied.Should().BeFalse();
        updated.Should().Be(ChatSettings.Default);
    }
}
