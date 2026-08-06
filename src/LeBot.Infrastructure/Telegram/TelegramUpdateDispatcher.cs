using System.Globalization;
using LeBot.Application.Metrics;
using LeBot.Application.Telemetry;
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
    IRepostEventStore eventStore,
    BotHealthSignal health,
    IOptions<TelegramOptions> options,
    TimeProvider timeProvider,
    ILogger<TelegramUpdateDispatcher> logger)
    : BackgroundService
{
    private static readonly TimeSpan BackoffOnError = TimeSpan.FromSeconds(5);

    // Defaults and caps for the on-demand dashboard reads, kept small so a reply stays well under
    // Telegram's 4096-char ceiling and a typo can't ask for thousands of rows.
    private const int DefaultListSize = 5;
    private const int MaxListSize = 15;

    /// <summary>"By failure rate" ignores hosts below this many posts, so a single 1-of-1 failure can't top it.</summary>
    private const int FailureRateMinVolume = 3;

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
        var parts = text.Split([' ', '\n'], 2, StringSplitOptions.RemoveEmptyEntries);
        var command = parts[0].TrimStart('/');
        var at = command.IndexOf('@', StringComparison.Ordinal);
        if (at >= 0)
        {
            command = command[..at];
        }

        var argument = parts.Length > 1 ? parts[1] : null;

        switch (command.ToLowerInvariant())
        {
            case "ping":
            case "start":
                await ReplyAsync(message, "🟢 OK", cancellationToken);
                return true;
            case "stats":
                await ReplyAsync(message, await BuildStatsAsync(cancellationToken), cancellationToken);
                return true;
            case "failures":
                await ReplyAsync(message, await BuildFailuresAsync(argument, cancellationToken), cancellationToken);
                return true;
            case "top":
                await ReplyAsync(message, await BuildTopAsync(cancellationToken), cancellationToken);
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

    private async Task<string> BuildStatsAsync(CancellationToken cancellationToken)
    {
        var uptime = timeProvider.GetUtcNow() - metrics.StartedAt;
        var allTime = await eventStore.GetStatsAsync(DateTimeOffset.MinValue, cancellationToken);
        return DashboardReportFormatter.Stats(metrics, uptime, allTime);
    }

    private async Task<string> BuildFailuresAsync(string? argument, CancellationToken cancellationToken)
    {
        var limit = ParseListSize(argument);
        var failures = await eventStore.GetRecentFailuresAsync(limit, cancellationToken);
        return DashboardReportFormatter.Failures(failures, timeProvider.GetUtcNow());
    }

    private async Task<string> BuildTopAsync(CancellationToken cancellationToken)
    {
        var byVolume = await eventStore.GetTopHostsByVolumeAsync(DefaultListSize, DateTimeOffset.MinValue, cancellationToken);
        var byFailureRate = await eventStore.GetTopHostsByFailureRateAsync(
            DefaultListSize, FailureRateMinVolume, DateTimeOffset.MinValue, cancellationToken);
        return DashboardReportFormatter.Top(byVolume, byFailureRate, FailureRateMinVolume);
    }

    /// <summary>Reads an optional "/failures N" count, defaulting and clamping it to a safe range.</summary>
    private static int ParseListSize(string? argument) =>
        int.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? Math.Clamp(n, 1, MaxListSize)
            : DefaultListSize;
}
