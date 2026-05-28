using LeBot.Application.UseCases.HandleIncomingMessage;
using LeBot.Infrastructure.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace LeBot.Infrastructure.Telegram;

/// <summary>
/// Long-polling background service. Pulls updates, hands each one to the application
/// handler inside a logging scope, and survives transient errors with a small backoff
/// instead of crashing the whole bot.
/// </summary>
public sealed class TelegramUpdateDispatcher(
    ITelegramBotClient bot,
    HandleIncomingMessageHandler handler,
    IOptions<TelegramOptions> options,
    ILogger<TelegramUpdateDispatcher> logger)
    : BackgroundService
{
    private static readonly TimeSpan BackoffOnError = TimeSpan.FromSeconds(5);
    private readonly TelegramOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var me = await bot.GetMe(stoppingToken);
        logger.LogInformation("Bot @{Username} (id {Id}) is online", me.Username, me.Id);

        var offset = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var updates = await bot.GetUpdates(
                    offset: offset,
                    limit: 100,
                    timeout: _options.PollingTimeoutSeconds,
                    allowedUpdates: [UpdateType.Message],
                    cancellationToken: stoppingToken);

                foreach (var update in updates)
                {
                    await DispatchAsync(update, stoppingToken);
                    offset = update.Id + 1;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Polling loop error; backing off {Backoff}s before retry", BackoffOnError.TotalSeconds);
                try
                {
                    await Task.Delay(BackoffOnError, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task DispatchAsync(Update update, CancellationToken cancellationToken)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = update.Id,
        });

        if (update.Message is not { Text: { Length: > 0 } text } message)
        {
            return;
        }

        try
        {
            await handler.HandleAsync(
                new IncomingMessage(
                    ChatId: message.Chat.Id,
                    MessageId: message.MessageId,
                    Text: text,
                    SenderUsername: message.From?.Username ?? "<unknown>"),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Unhandled error processing update {UpdateId}", update.Id);
        }
    }
}
