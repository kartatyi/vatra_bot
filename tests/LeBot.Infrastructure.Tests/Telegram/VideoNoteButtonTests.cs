using LeBot.Domain.Media;
using LeBot.Infrastructure.Telegram;

namespace LeBot.Infrastructure.Tests.Telegram;

public class VideoNoteButtonTests
{
    private static readonly Uri Stream = new("https://www.youtube.com/watch?v=live");

    [Fact]
    public void For_LongTitle_IsCutToOneButtonLine()
    {
        var payload = Payload(new string('x', 200));

        var label = VideoNoteButton.For(payload).InlineKeyboard.Single().Single().Text;

        label.Should().Be("🔴 " + new string('x', VideoNoteButton.MaxTitleLength - 1) + "…");
    }

    [Fact]
    public void For_TitleOnSeveralLines_JoinsThemIntoOne()
    {
        var label = VideoNoteButton.For(Payload("GTA 6\nlaunch")).InlineKeyboard.Single().Single().Text;

        label.Should().Be("🔴 GTA 6 launch");
    }

    [Fact]
    public void For_TitleAlreadyOpensWithARedDot_ShowsOnlyOne()
    {
        var label = VideoNoteButton.For(Payload("🔴 GTA 6 launch")).InlineKeyboard.Single().Single().Text;

        label.Should().Be("🔴 GTA 6 launch");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void For_NoTitle_SaysLive(string? title)
    {
        var label = VideoNoteButton.For(Payload(title)).InlineKeyboard.Single().Single().Text;

        label.Should().Be("🔴 Live");
    }

    [Fact]
    public void For_Always_OpensTheStream()
    {
        var url = VideoNoteButton.For(Payload("GTA 6")).InlineKeyboard.Single().Single().Url;

        url.Should().Be(Stream.AbsoluteUri);
    }

    private static MediaPayload Payload(string? title) =>
        new(Stream, title, null, [new MediaItem("live.note.mp4", MediaKind.VideoNote, "video/mp4", 1, 30)]) { IsLive = true };
}
