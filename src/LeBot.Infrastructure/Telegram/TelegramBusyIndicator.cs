using LeBot.Application.Ports;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace LeBot.Infrastructure.Telegram;

/// <summary>
/// Keeps a Telegram chat-action ("Bot is uploading a video...") alive by re-sending it
/// periodically. Telegram clears each action after ~5 seconds, so the loop fires every
/// four seconds for a comfortable overlap. Disposing stops the loop immediately.
/// </summary>
internal sealed class TelegramBusyIndicator : IAsyncDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(4);

    private readonly ITelegramBotClient _bot;
    private readonly long _chatId;
    private readonly ChatAction _action;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts;
    private readonly Task _loop;

    public TelegramBusyIndicator(
        ITelegramBotClient bot,
        long chatId,
        ChatAction action,
        ILogger logger)
    {
        _bot = bot;
        _chatId = chatId;
        _action = action;
        _logger = logger;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await _bot.SendChatAction(_chatId, _action, cancellationToken: _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // The indicator is cosmetic; one failed ping shouldn't break the action surface.
                _logger.LogDebug(ex, "SendChatAction failed for chat {ChatId}", _chatId);
            }

            try
            {
                await Task.Delay(RefreshInterval, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _cts.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed; that's fine.
        }

        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
            // expected
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Busy indicator loop ended with exception for chat {ChatId}", _chatId);
        }

        _cts.Dispose();
    }
}
