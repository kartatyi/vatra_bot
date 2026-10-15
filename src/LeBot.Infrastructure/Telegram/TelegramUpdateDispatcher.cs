using System.Globalization;
using LeBot.Application.Metrics;
using LeBot.Application.Ports;
using LeBot.Application.Telemetry;
using LeBot.Application.UseCases.HandleIncomingMessage;
using LeBot.Infrastructure.Configuration;
using LeBot.Infrastructure.Maintenance;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

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
    IStatsOptOutStore optOuts,
    IChatSettingsStore settingsStore,
    IRevealableTextStore revealableTexts,
    IAppVersion appVersion,
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

    /// <summary>The default <c>/leaders</c> window; <c>/leaders all</c> lifts it.</summary>
    private static readonly TimeSpan LeadersWindow = TimeSpan.FromDays(30);

    /// <summary>How many people <c>/leaders</c> lists.</summary>
    private const int LeadersShown = 10;

    /// <summary>
    /// How many it ranks before resolving names: a few spare, since people who left the chat drop out
    /// at that step and the list should still fill up.
    /// </summary>
    private const int LeadersCandidates = 15;

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
                    allowedUpdates: [UpdateType.Message, UpdateType.CallbackQuery],
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

        try
        {
            if (update.CallbackQuery is { } callback)
            {
                await HandleCallbackAsync(callback, cancellationToken);
                return;
            }

            if (update.Message is not { Text: { Length: > 0 } text } message)
            {
                return;
            }

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
                    SenderUsername: message.From?.Username ?? "<unknown>",
                    // Bots — the anonymous-admin stand-in included — and channel posts aren't people to rank.
                    SenderId: message.From is { IsBot: false } sender ? sender.Id : null),
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
                await ReplyAsync(message, "🟢 OK", cancellationToken);
                return true;
            case "start":
            case "help":
                await ReplyAsync(message, await BuildHelpAsync(message.From?.LanguageCode, cancellationToken), cancellationToken);
                return true;
            case "stats":
                await ReplyAsync(message, await BuildStatsAsync(cancellationToken), cancellationToken);
                return true;
            case "failures":
                await ReplyAsync(message, await BuildFailuresAsync(message.Chat.Id, argument, cancellationToken), cancellationToken);
                return true;
            case "top":
                await ReplyAsync(message, await BuildTopAsync(cancellationToken), cancellationToken);
                return true;
            case "leaders":
                await ReplyAsync(message, await BuildLeadersAsync(message, argument, cancellationToken), cancellationToken);
                return true;
            case "settings":
                var settings = await settingsStore.GetAsync(message.Chat.Id, cancellationToken);
                await ReplyAsync(message, ChatSettingsKeyboard.Text, cancellationToken, ChatSettingsKeyboard.Markup(settings));
                return true;
            default:
                return false;
        }
    }

    private Task<Message> ReplyAsync(
        Message original,
        string text,
        CancellationToken cancellationToken,
        InlineKeyboardMarkup? keyboard = null) =>
        bot.SendMessage(
            chatId: original.Chat.Id,
            text: text,
            replyParameters: new ReplyParameters { MessageId = original.MessageId },
            replyMarkup: keyboard,
            cancellationToken: cancellationToken);

    /// <summary>
    /// A press on one of the bot's inline buttons: a repost's "show text", or a <c>/settings</c> choice.
    /// Every press gets an answer — Telegram keeps the button spinning until it does — but only a group
    /// admin's press changes settings.
    /// </summary>
    private async Task HandleCallbackAsync(CallbackQuery callback, CancellationToken cancellationToken)
    {
        if (callback.Message is not { } origin)
        {
            await bot.AnswerCallbackQuery(callback.Id, cancellationToken: cancellationToken);
            return;
        }

        if (callback.Data == RevealButton.CallbackData)
        {
            await RevealAsync(callback, origin, cancellationToken);
            return;
        }

        var current = await settingsStore.GetAsync(origin.Chat.Id, cancellationToken);
        if (!ChatSettingsKeyboard.TryApply(callback.Data, current, out var updated))
        {
            await bot.AnswerCallbackQuery(callback.Id, cancellationToken: cancellationToken);
            return;
        }

        if (!await IsChatAdminAsync(origin.Chat, callback.From.Id, cancellationToken))
        {
            await bot.AnswerCallbackQuery(
                callback.Id, ChatSettingsKeyboard.AdminsOnly, showAlert: true, cancellationToken: cancellationToken);
            return;
        }

        if (updated != current)
        {
            await settingsStore.SaveAsync(origin.Chat.Id, updated, cancellationToken);
            await bot.EditMessageReplyMarkup(
                origin.Chat.Id, origin.MessageId, ChatSettingsKeyboard.Markup(updated), cancellationToken: cancellationToken);
            logger.LogInformation(
                "Chat {ChatId} settings changed: video {VideoCaption}, post {PostCaption}",
                origin.Chat.Id, updated.VideoCaption, updated.PostCaption);
        }

        await bot.AnswerCallbackQuery(callback.Id, ChatSettingsKeyboard.Saved, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Swaps a repost's held-back text into the repost itself. Anyone in the chat may press it — it only
    /// shows what the post already said. Editing without a keyboard also removes the button.
    /// </summary>
    private async Task RevealAsync(CallbackQuery callback, Message origin, CancellationToken cancellationToken)
    {
        var text = await revealableTexts.TakeAsync(origin.Chat.Id, origin.MessageId, cancellationToken);
        if (text is null)
        {
            await bot.AnswerCallbackQuery(callback.Id, RevealButton.Gone, cancellationToken: cancellationToken);
            return;
        }

        // A text reply keeps its body in Text, a media reply in its caption — and their limits differ.
        if (origin.Text is not null)
        {
            await bot.EditMessageText(
                origin.Chat.Id,
                origin.MessageId,
                TelegramBotMessenger.Fit(text, TelegramBotMessenger.MaxTextLength)!, // non-null in, non-null out
                cancellationToken: cancellationToken);
        }
        else
        {
            await bot.EditMessageCaption(
                origin.Chat.Id,
                origin.MessageId,
                TelegramBotMessenger.Fit(text, TelegramBotMessenger.MaxCaptionLength),
                cancellationToken: cancellationToken);
        }

        await bot.AnswerCallbackQuery(callback.Id, cancellationToken: cancellationToken);
    }

    /// <summary>In a private chat the user owns the chat; in a group only its creator and admins count.</summary>
    private async Task<bool> IsChatAdminAsync(Chat chat, long userId, CancellationToken cancellationToken)
    {
        if (chat.Type == ChatType.Private)
        {
            return true;
        }

        var member = await bot.GetChatMember(chat.Id, userId, cancellationToken);
        return member.Status is ChatMemberStatus.Administrator or ChatMemberStatus.Creator;
    }

    private async Task<string> BuildHelpAsync(string? languageCode, CancellationToken cancellationToken)
    {
        // Telegram answers a language with no dedicated copy with an empty one rather than the default,
        // so fall back by hand: the asker's language first, then the language-less default.
        var description = (await bot.GetMyDescription(languageCode, cancellationToken)).Description;
        if (string.IsNullOrWhiteSpace(description) && languageCode is not null)
        {
            description = (await bot.GetMyDescription(cancellationToken: cancellationToken)).Description;
        }

        var commands = await bot.GetMyCommands(languageCode: languageCode, cancellationToken: cancellationToken);
        if (commands.Length == 0 && languageCode is not null)
        {
            commands = await bot.GetMyCommands(cancellationToken: cancellationToken);
        }

        return HelpReportFormatter.Help(description, commands, appVersion.Current);
    }

    private async Task<string> BuildStatsAsync(CancellationToken cancellationToken)
    {
        var uptime = timeProvider.GetUtcNow() - metrics.StartedAt;
        var allTime = await eventStore.GetStatsAsync(DateTimeOffset.MinValue, cancellationToken);
        return DashboardReportFormatter.Stats(metrics, uptime, allTime);
    }

    private async Task<string> BuildFailuresAsync(long chatId, string? argument, CancellationToken cancellationToken)
    {
        var limit = ParseListSize(argument);

        // Scoped to the asking chat: a failure row carries the full URL someone posted, and one group
        // has no business reading another's.
        var failures = await eventStore.GetRecentFailuresAsync(limit, ChatHasher.Of(chatId), cancellationToken);
        return DashboardReportFormatter.Failures(failures, timeProvider.GetUtcNow());
    }

    private async Task<string> BuildLeadersAsync(Message message, string? argument, CancellationToken cancellationToken)
    {
        var option = argument?.Trim().ToLowerInvariant();
        if (option is "optout" or "optin")
        {
            if (message.From is not { IsBot: false } sender)
            {
                return LeaderboardFormatter.NoSender;
            }

            var optingOut = option == "optout";
            await optOuts.SetOptedOutAsync(message.Chat.Id, sender.Id, optingOut, cancellationToken);
            return optingOut ? LeaderboardFormatter.OptedOut : LeaderboardFormatter.OptedIn;
        }

        var allTime = option == "all";
        var since = allTime ? DateTimeOffset.MinValue : timeProvider.GetUtcNow() - LeadersWindow;
        var rows = await eventStore.GetSenderHostCountsAsync(ChatHasher.Of(message.Chat.Id), since, cancellationToken);

        // Names are asked of Telegram now rather than stored: the journal keeps only ids, a renamed person
        // shows their current name, and someone who left the chat drops out of the ranking.
        var ranking = new List<(string Name, LeaderboardEntry Entry)>();
        foreach (var entry in Leaderboard.Rank(rows, LeadersCandidates))
        {
            if (ranking.Count == LeadersShown)
            {
                break;
            }

            if (await TryGetMemberNameAsync(message.Chat.Id, entry.SenderId, cancellationToken) is { } name)
            {
                ranking.Add((name, entry));
            }
        }

        return LeaderboardFormatter.Format(ranking, allTime);
    }

    /// <summary>The member's current display name, or <see langword="null"/> when they're no longer in the chat.</summary>
    private async Task<string?> TryGetMemberNameAsync(long chatId, long userId, CancellationToken cancellationToken)
    {
        try
        {
            var member = await bot.GetChatMember(chatId, userId, cancellationToken);
            if (member.Status is ChatMemberStatus.Left or ChatMemberStatus.Kicked)
            {
                return null;
            }

            return string.IsNullOrWhiteSpace(member.User.LastName)
                ? member.User.FirstName
                : $"{member.User.FirstName} {member.User.LastName}";
        }
        catch (ApiRequestException ex)
        {
            // "user not found" and friends: treat as gone. The id stays out of Information — it's PII.
            logger.LogDebug(ex, "Could not resolve member {UserId} for the ranking", userId);
            return null;
        }
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
