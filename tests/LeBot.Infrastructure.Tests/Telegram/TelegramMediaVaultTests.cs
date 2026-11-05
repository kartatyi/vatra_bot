using LeBot.Domain.Media;
using LeBot.Infrastructure.Configuration;
using LeBot.Infrastructure.Telegram;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Telegram.Bot;

namespace LeBot.Infrastructure.Tests.Telegram;

public class TelegramMediaVaultTests
{
    private static readonly Uri Source = new("https://example.com/x");

    private readonly ITelegramBotClient _bot = Substitute.For<ITelegramBotClient>();

    [Fact]
    public async Task Store_NoStorageChat_DoesNothing()
    {
        var vault = new TelegramMediaVault(_bot, Options.Create(new StorageOptions()), NullLogger<TelegramMediaVault>.Instance);
        var payload = new MediaPayload(Source, null, null, [new MediaItem("x.mp4", MediaKind.Video, null, null, null)]);

        (await vault.StoreAsync(payload, CancellationToken.None)).Should().BeNull();
        vault.IsConfigured.Should().BeFalse();
        _bot.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Store_ItemsAlreadyInTelegram_PassThroughWithoutAnUpload()
    {
        var vault = new TelegramMediaVault(_bot, Options.Create(new StorageOptions { ChatId = -1 }), NullLogger<TelegramMediaVault>.Instance);
        var item = new MediaItem(string.Empty, MediaKind.Photo, null, null, null) { TelegramFileId = "ph-1" };

        var stored = await vault.StoreAsync(new MediaPayload(Source, null, null, [item]), CancellationToken.None);

        stored.Should().Equal(item);
        _bot.ReceivedCalls().Should().BeEmpty();
    }
}
