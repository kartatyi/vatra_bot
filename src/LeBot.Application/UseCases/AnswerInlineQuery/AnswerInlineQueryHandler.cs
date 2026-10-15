using LeBot.Application.Ports;
using Microsoft.Extensions.Logging;

namespace LeBot.Application.UseCases.AnswerInlineQuery;

/// <summary>
/// The inline use-case: someone typed <c>@bot &lt;link&gt;</c> in any chat. Offers the post ready to send
/// when it can be prepared within <see cref="WaitBudget"/>; otherwise says it isn't ready yet and lets the
/// preparation carry on, so the next query for the link finds it done.
/// </summary>
public sealed class AnswerInlineQueryHandler(
    IUrlExtractor urlExtractor,
    InlinePostPreparer preparer,
    IInlineAnswerer answerer,
    TimeProvider timeProvider,
    ILogger<AnswerInlineQueryHandler> logger)
{
    /// <summary>How long an answer waits for a preparation — comfortably inside Telegram's patience for inline answers.</summary>
    public static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(8);

    public async Task HandleAsync(InlineQuery query, CancellationToken cancellationToken)
    {
        if (urlExtractor.Extract(query.Text) is not [var url, ..])
        {
            await answerer.AnswerAsync(query.Id, new InlineAnswer.NoLink(), cancellationToken);
            return;
        }

        var preparing = preparer.PrepareAsync(url);
        var budget = Task.Delay(WaitBudget, timeProvider, cancellationToken);
        InlineAnswer answer = await Task.WhenAny(preparing, budget) != preparing
            ? new InlineAnswer.NotReady(url)
            : await preparing is { } post
                ? new InlineAnswer.Ready(post)
                : new InlineAnswer.Failed(url);

        logger.LogDebug("Answering inline query for {Url} with {Answer}", url, answer.GetType().Name);
        await answerer.AnswerAsync(query.Id, answer, cancellationToken);
    }
}
