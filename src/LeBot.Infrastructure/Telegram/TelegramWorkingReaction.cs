using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;

namespace LeBot.Infrastructure.Telegram;

/// <summary>
/// A 👀 reaction on the message whose link the bot is working on, removed again on dispose. The
/// chat-header "uploading video" status is easy to miss; a reaction on the link itself isn't — it says
/// "seen it, on it" for as long as a slow extraction takes. Cosmetic, so every failure is swallowed:
/// a chat can restrict which reactions are allowed, and that must never cost the repost.
/// </summary>
internal sealed class TelegramWorkingReaction : IAsyncDisposable
{
    internal const string Emoji = "👀";

    private readonly ITelegramBotClient _bot;
    private readonly long _chatId;
    private readonly int _messageId;
    private readonly ILogger _logger;
    private readonly bool _placed;

    private TelegramWorkingReaction(ITelegramBotClient bot, long chatId, int messageId, ILogger logger, bool placed)
    {
        _bot = bot;
        _chatId = chatId;
        _messageId = messageId;
        _logger = logger;
        _placed = placed;
    }

    /// <summary>Places the reaction; the returned handle removes it — or does nothing if placing failed.</summary>
    public static async Task<TelegramWorkingReaction> PlaceAsync(
        ITelegramBotClient bot,
        long chatId,
        int messageId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            await bot.SetMessageReaction(chatId, messageId, [new ReactionTypeEmoji { Emoji = Emoji }], cancellationToken: cancellationToken);
            return new TelegramWorkingReaction(bot, chatId, messageId, logger, placed: true);
        }
        catch (RequestException ex)
        {
            logger.LogDebug(ex, "Could not place the working reaction in chat {ChatId}", chatId);
            return new TelegramWorkingReaction(bot, chatId, messageId, logger, placed: false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_placed)
        {
            return;
        }

        try
        {
            // No token: removal must run even when the work it marked was cancelled.
            await _bot.SetMessageReaction(_chatId, _messageId, [], cancellationToken: CancellationToken.None);
        }
        catch (RequestException ex)
        {
            _logger.LogDebug(ex, "Could not remove the working reaction in chat {ChatId}", _chatId);
        }
    }
}
