using LeBot.Application.Digest;
using LeBot.Application.Telemetry;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types.Enums;

namespace LeBot.Infrastructure.Telegram;

/// <summary>
/// Posts the weekly digest every Monday at <see cref="DigestSchedule.PostAt"/> local time to each chat that
/// turned it on. A restart that straddles the posting moment skips that week rather than posting twice —
/// a missed summary is harmless, a duplicate one is noise.
/// </summary>
internal sealed class WeeklyDigestService(
    ITelegramBotClient bot,
    WeeklyDigestPlanner planner,
    StatsTimeZone timeZone,
    TimeProvider timeProvider,
    ILogger<WeeklyDigestService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = timeProvider.GetUtcNow();
            var next = DigestSchedule.NextRun(now, timeZone);
            try
            {
                await Task.Delay(next - now, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await PostDueDigestsAsync(stoppingToken);
        }
    }

    private async Task PostDueDigestsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<(long ChatId, WeeklyDigest Digest)> due;
        try
        {
            due = await planner.PlanAsync(timeProvider.GetUtcNow(), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A scheduled job is its own boundary: log, and try again next week.
            logger.LogError(ex, "Planning the weekly digests failed");
            return;
        }

        foreach (var (chatId, digest) in due)
        {
            try
            {
                var names = await NameDroppersAsync(chatId, digest, cancellationToken);
                await bot.SendMessage(chatId, DigestFormatter.Format(digest, names), cancellationToken: cancellationToken);
                logger.LogInformation("Posted the weekly digest to chat {ChatId}", chatId);
            }
            catch (RequestException ex)
            {
                // One chat kicking the bot out mustn't cost every other chat its digest.
                logger.LogWarning(ex, "Could not post the weekly digest to chat {ChatId}", chatId);
            }
        }
    }

    /// <summary>The top droppers' current names; anyone who left the chat is left out, as in <c>/leaders</c>.</summary>
    private async Task<List<(string Name, int Reposts)>> NameDroppersAsync(long chatId, WeeklyDigest digest, CancellationToken cancellationToken)
    {
        var names = new List<(string, int)>();
        foreach (var (senderId, reposts) in digest.TopDroppers)
        {
            try
            {
                var member = await bot.GetChatMember(chatId, senderId, cancellationToken);
                if (member.Status is not (ChatMemberStatus.Left or ChatMemberStatus.Kicked))
                {
                    var user = member.User;
                    names.Add((string.IsNullOrWhiteSpace(user.LastName) ? user.FirstName : $"{user.FirstName} {user.LastName}", reposts));
                }
            }
            catch (ApiRequestException ex)
            {
                logger.LogDebug(ex, "Could not name a digest dropper in chat {ChatId}", chatId);
            }
        }

        return names;
    }
}
