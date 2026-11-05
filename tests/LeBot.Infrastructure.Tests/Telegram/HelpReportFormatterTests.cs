using LeBot.Domain.Common;
using LeBot.Infrastructure.Telegram;
using Telegram.Bot.Types;

namespace LeBot.Infrastructure.Tests.Telegram;

public class HelpReportFormatterTests
{
    private static readonly ReleaseVersion Released = new(1, 2, 3);

    [Fact]
    public void Help_WithProfile_ListsDescriptionCommandsAndVersion()
    {
        BotCommand[] commands =
        [
            new() { Command = "stats", Description = "How many links I've handled" },
            new() { Command = "ping", Description = "Check I'm online" },
        ];

        var help = HelpReportFormatter.Help("  I turn links into videos.\n", commands, Released);

        help.Should().Be(
            "I turn links into videos.\n\n" +
            "/stats — How many links I've handled\n" +
            "/ping — Check I'm online\n\n" +
            "v1.2.3");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Help_BlankDescription_UsesFallback(string? description)
    {
        var help = HelpReportFormatter.Help(description, [], Released);

        help.Should().StartWith(HelpReportFormatter.FallbackDescription);
    }

    [Fact]
    public void Help_NoCommands_SkipsTheCommandBlock()
    {
        var help = HelpReportFormatter.Help("About me.", [], Released);

        help.Should().Be("About me.\n\nv1.2.3");
    }

    [Fact]
    public void Help_DevBuild_OmitsVersion()
    {
        var help = HelpReportFormatter.Help("About me.", [], new ReleaseVersion(0, 0, 0));

        help.Should().Be("About me.");
    }
}
