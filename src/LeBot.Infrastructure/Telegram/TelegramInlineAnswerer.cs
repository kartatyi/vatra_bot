using LeBot.Application.Ports;
using LeBot.Application.UseCases.AnswerInlineQuery;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;

namespace LeBot.Infrastructure.Telegram;

/// <summary><see cref="IInlineAnswerer"/> over the Bot API; the results themselves come from <see cref="InlineResults"/>.</summary>
internal sealed class TelegramInlineAnswerer(
    ITelegramBotClient bot,
    ILogger<TelegramInlineAnswerer> logger) : IInlineAnswerer
{
    public async Task AnswerAsync(string queryId, InlineAnswer answer, string? languageCode, CancellationToken cancellationToken)
    {
        var (results, cacheSeconds) = InlineResults.For(answer, BotLanguages.From(languageCode));
        try
        {
            await bot.AnswerInlineQuery(queryId, results, cacheTime: cacheSeconds, cancellationToken: cancellationToken);
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 400)
        {
            // "query is too old": the user typed on and Telegram moved to a newer query. Nothing to do.
            logger.LogDebug(ex, "Inline query {QueryId} expired before the answer", queryId);
        }
    }
}
