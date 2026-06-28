using System.Globalization;
using System.Text;
using LeBot.Application.Metrics;
using LeBot.Application.UseCases.HandleIncomingMessage;
using LeBot.Infrastructure.Configuration;
using LeBot.Infrastructure.Maintenance;
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
    RepostMetrics metrics,
    BotHealthSignal health,
    IOptions<TelegramOptions> options,
    TimeProvider timeProvider,
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
        var announcedServing = false;

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

                if (!announcedServing)
                {
                    // getMe succeeded and the first poll returned — the bot is genuinely serving, not
                    // just launched. The self-updater waits on this before promoting a fresh build.
                    health.MarkServing();
                    announcedServing = true;
                }

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
            if (text.StartsWith('/'))
            {
                if (await TryHandleCommandAsync(message, text, cancellationToken))
                {
                    return;
                }
            }

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

    private async Task<bool> TryHandleCommandAsync(Message message, string text, CancellationToken cancellationToken)
    {
        var token = text.Split([' ', '\n'], 2, StringSplitOptions.RemoveEmptyEntries)[0];
        var command = token.TrimStart('/');
        var at = command.IndexOf('@', StringComparison.Ordinal);
        if (at >= 0)
        {
            command = command[..at];
        }

        switch (command.ToLowerInvariant())
        {
            case "ping":
            case "start":
                await ReplyAsync(message, "🟢 OK", cancellationToken);
                return true;
            case "stats":
                await ReplyAsync(message, FormatStats(), cancellationToken);
                return true;
            default:
                return false;
        }
    }

    private Task<Message> ReplyAsync(Message original, string text, CancellationToken cancellationToken) =>
        bot.SendMessage(
            chatId: original.Chat.Id,
            text: text,
            replyParameters: new ReplyParameters { MessageId = original.MessageId },
            cancellationToken: cancellationToken);

    private string FormatStats()
    {
        var uptime = timeProvider.GetUtcNow() - metrics.StartedAt;
        var sb = new StringBuilder();
        sb.Append("Uptime: ");
        sb.AppendLine(FormatUptime(uptime));
        sb.Append("Media reposts: ").Append(metrics.MediaReposts.ToString(CultureInfo.InvariantCulture)).AppendLine();
        sb.Append("Text reposts: ").Append(metrics.TextReposts.ToString(CultureInfo.InvariantCulture)).AppendLine();
        sb.Append("Fallback acks: ").Append(metrics.FallbackAcks.ToString(CultureInfo.InvariantCulture)).AppendLine();
        sb.Append("Failures: ").Append(metrics.Failures.ToString(CultureInfo.InvariantCulture)).AppendLine();
        sb.Append("Silent skips: ").Append(metrics.SilentSkips.ToString(CultureInfo.InvariantCulture)).AppendLine();

        if (metrics.ByExtractor.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("By extractor:");
            foreach (var pair in metrics.ByExtractor.OrderByDescending(kv => kv.Value))
            {
                sb.Append("  ").Append(pair.Key).Append(": ").AppendLine(pair.Value.ToString(CultureInfo.InvariantCulture));
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string FormatUptime(TimeSpan uptime) =>
        uptime.TotalDays >= 1
            ? $"{(int)uptime.TotalDays}d {uptime:hh\\:mm}"
            : uptime.ToString("hh\\:mm\\:ss", CultureInfo.InvariantCulture);
}
