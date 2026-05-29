using LeBot.Application.Metrics;
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
    RepostMetrics metrics,
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
        var candidates = extractors.Where(e => e.CanHandle(url)).ToList();
        if (candidates.Count == 0)
        {
            logger.LogDebug("No extractor for URL {Url}", url);
            return;
        }

        // Show "Bot is uploading a video..." in the chat header for the whole life of this
        // method so the user has immediate feedback while extraction + upload run. The
        // indicator is cancelled when ProcessUrlAsync returns (success, failure, or fallback).
        await using var busy = messenger.IndicateBusy(message.ChatId, BusyKind.UploadingVideo);

        MediaPayload? textFallback = null;
        var sawSubstantiveAttempt = false;

        foreach (var extractor in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await extractor.ExtractAsync(url, cancellationToken);
            switch (result)
            {
                case Result<MediaPayload, ExtractionError>.Ok ok when ok.Value.HasMedia:
                    await messenger.ReplyWithMediaAsync(
                        message.ChatId,
                        message.MessageId,
                        ok.Value,
                        cancellationToken);
                    metrics.RecordMediaRepost(extractor.GetType().Name);
                    logger.LogInformation(
                        "Reposted {Count} media item(s) from {Url} via {Extractor} into chat {ChatId}",
                        ok.Value.Items.Count, url, extractor.GetType().Name, message.ChatId);
                    return;

                case Result<MediaPayload, ExtractionError>.Ok ok when HasReplyableText(ok.Value):
                    textFallback ??= ok.Value;
                    sawSubstantiveAttempt = true;
                    break;

                case Result<MediaPayload, ExtractionError>.Err err when err.Error is ExtractionError.UnsupportedPlatform:
                    // This extractor doesn't claim the URL; treat it as if CanHandle had returned
                    // false. Silent skip — no ack, no warning.
                    logger.LogDebug(
                        "{Extractor} marked {Url} as unsupported", extractor.GetType().Name, url);
                    break;

                case Result<MediaPayload, ExtractionError>.Err err:
                    logger.LogWarning(
                        "{Extractor} failed for {Url}: {Reason}",
                        extractor.GetType().Name, url, err.Error.Reason);
                    metrics.RecordFailure(extractor.GetType().Name);
                    sawSubstantiveAttempt = true;
                    break;

                case Result<MediaPayload, ExtractionError>.Ok:
                    // Extractor returned nothing usable; try the next candidate.
                    sawSubstantiveAttempt = true;
                    break;
            }
        }

        if (textFallback is not null)
        {
            await messenger.ReplyWithTextAsync(
                message.ChatId,
                message.MessageId,
                textFallback,
                cancellationToken);
            metrics.RecordTextRepost();
            logger.LogInformation(
                "Reposted text body from {Url} into chat {ChatId}",
                url, message.ChatId);
            return;
        }

        if (!sawSubstantiveAttempt)
        {
            // Every extractor declined the URL — bare http link to a non-media site, basically.
            // Stay silent so the chat isn't flooded with "Couldn't extract" for github / news /
            // blog URLs that nobody wanted reposted in the first place.
            metrics.RecordSilentSkip();
            logger.LogDebug("No extractor claimed {Url} — silent skip", url);
            return;
        }

        // Final acknowledgement so the user always sees a reply for any URL an extractor tried
        // to extract from and couldn't.
        var fallback = new MediaPayload(
            SourceUrl: url,
            Title: null,
            Author: null,
            Items: [],
            Description: "Couldn't extract media from this link.");
        await messenger.ReplyWithTextAsync(
            message.ChatId,
            message.MessageId,
            fallback,
            cancellationToken);
        metrics.RecordFallbackAck();
        logger.LogInformation(
            "Sent extraction-failed acknowledgement for {Url} into chat {ChatId}",
            url, message.ChatId);
    }

    private static bool HasReplyableText(MediaPayload payload)
    {
        return !string.IsNullOrWhiteSpace(payload.Description)
            || !string.IsNullOrWhiteSpace(payload.Title);
    }
}
