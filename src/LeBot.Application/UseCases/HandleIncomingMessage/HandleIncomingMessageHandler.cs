using LeBot.Application.Ports;
using LeBot.Domain.Common;
using LeBot.Domain.Media;
using Microsoft.Extensions.Logging;

namespace LeBot.Application.UseCases.HandleIncomingMessage;

/// <summary>
/// The Phase-1 use-case: for every URL we recognise in a chat message,
/// extract media and reply with it. The handler stays free of I/O concerns —
/// URL parsing, extraction, and sending are all injected ports.
/// </summary>
public sealed class HandleIncomingMessageHandler(
    IUrlExtractor urlExtractor,
    IEnumerable<IPlatformExtractor> extractors,
    ITelegramMessenger messenger,
    ILogger<HandleIncomingMessageHandler> logger)
{
    public async Task HandleAsync(IncomingMessage message, CancellationToken cancellationToken)
    {
        var urls = urlExtractor.Extract(message.Text);
        if (urls.Count == 0)
        {
            return;
        }

        foreach (var url in urls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessUrlAsync(message, url, cancellationToken);
        }
    }

    private async Task ProcessUrlAsync(IncomingMessage message, Uri url, CancellationToken cancellationToken)
    {
        var extractor = extractors.FirstOrDefault(e => e.CanHandle(url));
        if (extractor is null)
        {
            logger.LogDebug("No extractor for URL {Url}", url);
            return;
        }

        var result = await extractor.ExtractAsync(url, cancellationToken);

        switch (result)
        {
            case Result<MediaPayload, ExtractionError>.Ok ok when ok.Value.HasMedia:
                await messenger.ReplyWithMediaAsync(
                    message.ChatId,
                    message.MessageId,
                    ok.Value,
                    cancellationToken);
                logger.LogInformation(
                    "Reposted {Count} media item(s) from {Url} into chat {ChatId}",
                    ok.Value.Items.Count, url, message.ChatId);
                break;

            case Result<MediaPayload, ExtractionError>.Ok ok when HasReplyableText(ok.Value):
                await messenger.ReplyWithTextAsync(
                    message.ChatId,
                    message.MessageId,
                    ok.Value,
                    cancellationToken);
                logger.LogInformation(
                    "Reposted text body from {Url} into chat {ChatId}",
                    url, message.ChatId);
                break;

            case Result<MediaPayload, ExtractionError>.Ok:
                logger.LogInformation("Extractor returned no media for {Url}", url);
                break;

            case Result<MediaPayload, ExtractionError>.Err err:
                logger.LogWarning(
                    "Failed to extract media from {Url}: {Reason}",
                    url, err.Error.Reason);
                break;
        }
    }

    private static bool HasReplyableText(MediaPayload payload)
    {
        return !string.IsNullOrWhiteSpace(payload.Description)
            || !string.IsNullOrWhiteSpace(payload.Title);
    }
}
