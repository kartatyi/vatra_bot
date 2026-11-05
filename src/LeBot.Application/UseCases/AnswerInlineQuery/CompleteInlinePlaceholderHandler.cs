using LeBot.Application.Ports;
using Microsoft.Extensions.Logging;

namespace LeBot.Application.UseCases.AnswerInlineQuery;

/// <summary>
/// Finishes what a "loading" placeholder promised: waits for the link's preparation — usually already
/// running, started by the very query that offered the placeholder — then swaps the placeholder for the
/// post, or for "couldn't get media" when nothing came of it.
/// </summary>
public sealed class CompleteInlinePlaceholderHandler(
    IUrlExtractor urlExtractor,
    InlinePostPreparer preparer,
    IInlineMessageEditor editor,
    ILogger<CompleteInlinePlaceholderHandler> logger)
{
    public async Task HandleAsync(ChosenPlaceholder chosen, CancellationToken cancellationToken)
    {
        if (urlExtractor.Extract(chosen.Query) is not [var url, ..])
        {
            // Can't happen for a placeholder the bot offered — it only offers one for a query with a link.
            logger.LogDebug("Chosen placeholder carries no link; leaving it as it is");
            return;
        }

        if (await preparer.PrepareAsync(url) is { } post)
        {
            await editor.ShowPostAsync(chosen.InlineMessageId, post, chosen.WithText, url, chosen.LanguageCode, cancellationToken);
        }
        else
        {
            await editor.ShowFailureAsync(chosen.InlineMessageId, url, chosen.LanguageCode, cancellationToken);
        }
    }
}
