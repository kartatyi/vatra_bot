using LeBot.Infrastructure.MediaExtraction.Steam;

namespace LeBot.Infrastructure.Tests.MediaExtraction.Steam;

public class SteamAppUrlTests
{
    [Theory]
    [InlineData("https://store.steampowered.com/app/3834090/YAPYAP/", 3834090)]
    [InlineData("https://store.steampowered.com/app/3949040/RV_There_Yet/?snr=1_7_15__13", 3949040)]
    [InlineData("https://store.steampowered.com/app/570", 570)]
    [InlineData("https://store.steampowered.com/app/570/", 570)]
    [InlineData("http://store.steampowered.com/app/570/Dota_2/", 570)]
    [InlineData("https://STORE.steampowered.com/App/570/", 570)]
    [InlineData("https://store.steampowered.com/agecheck/app/1091500/", 1091500)]
    [InlineData("https://s.team/a/3949040", 3949040)]
    [InlineData("https://s.team/a/3949040/", 3949040)]
    public void TryGetAppId_StorePageLink_ReturnsTheAppId(string url, int expected)
    {
        var appId = SteamAppUrl.TryGetAppId(new Uri(url));

        appId.Should().Be(expected);
    }

    [Theory]
    [InlineData("https://store.steampowered.com/")]
    [InlineData("https://store.steampowered.com/bundle/232/Valve_Complete_Pack/")]
    [InlineData("https://store.steampowered.com/sub/469/")]
    [InlineData("https://store.steampowered.com/news/app/570/view/123456789")]
    [InlineData("https://store.steampowered.com/app/")]
    [InlineData("https://store.steampowered.com/app/0/")]
    [InlineData("https://store.steampowered.com/app/abc/")]
    [InlineData("https://store.steampowered.com/app/57012345678/")]
    [InlineData("https://steamcommunity.com/app/570")]
    [InlineData("https://s.team/p/abcd-efgh")]
    [InlineData("https://notstore.steampowered.com/app/570/")]
    [InlineData("https://example.com/app/570/")]
    public void TryGetAppId_AnythingButOneStorePage_ReturnsNull(string url)
    {
        var appId = SteamAppUrl.TryGetAppId(new Uri(url));

        appId.Should().BeNull();
    }
}
