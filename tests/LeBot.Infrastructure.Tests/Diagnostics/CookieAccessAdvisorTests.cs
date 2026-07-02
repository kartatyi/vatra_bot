using LeBot.Infrastructure.Diagnostics;

namespace LeBot.Infrastructure.Tests.Diagnostics;

public class CookieAccessAdvisorTests
{
    [Theory]
    [InlineData("firefox", true)]
    [InlineData("chrome", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void CookiesEnabled_TrueOnlyForNonBlankBrowser(string? browser, bool expected) =>
        CookieAccessAdvisor.CookiesEnabled(browser).Should().Be(expected);

    [Theory]
    [InlineData(true, "firefox", true)]    // the trap: LocalSystem + cookies configured
    [InlineData(false, "firefox", false)]  // interactive user can read its own browser
    [InlineData(true, null, false)]        // LocalSystem but no cookies asked for
    [InlineData(false, null, false)]
    public void ShouldWarnUnreadable_OnlyWhenLocalSystemAndCookiesSet(
        bool isLocalSystem,
        string? browser,
        bool expected) =>
        CookieAccessAdvisor.ShouldWarnUnreadable(isLocalSystem, browser).Should().Be(expected);

    [Fact]
    public void UnreadableWarning_NamesTheBrowserAndPointsAwayFromLocalSystem()
    {
        var warning = CookieAccessAdvisor.UnreadableWarning("firefox");

        warning.Should().Contain("firefox");
        warning.Should().Contain("LocalSystem");
        warning.Should().Contain("interactive user");
    }
}
