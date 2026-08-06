using LeBot.Application.Metrics;
using LeBot.Application.Ports;
using LeBot.Domain.Common;
using LeBot.Domain.Media;
using Microsoft.Extensions.Logging;

namespace LeBot.Application.UseCases.HandleIncomingMessage;

/// <summary>
/// The Phase-1 use-case: for every URL we recognise in a chat message,
/// extract media and reply with it. The handler stays free of I/O concerns —
/// URL parsing, extraction, caching, and sending are all injected ports.
/// </summary>
public sealed class HandleIncomingMessageHandler(
    IUrlExtractor urlExtractor,
    IEnumerable<IPlatformExtractor> extractors,
    IMediaCache cache,
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

        // Ask the cache before any extractor runs: a link the chat has already seen is answered
        // from disk without a single request to the platform.
        if (await TryServeFromCacheAsync(message, url, cancellationToken))
        {
            return;
        }

        (MediaPayload Payload, string Extractor)? textFallback = null;
        var sawSubstantiveAttempt = false;

        foreach (var extractor in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var extractorName = extractor.GetType().Name;
            var result = await extractor.ExtractAsync(url, cancellationToken);
            switch (result)
            {
                case Result<MediaPayload, ExtractionError>.Ok ok when ok.Value.HasMedia:
                    // Cache first: the messenger deletes the files it was handed once the upload
                    // finishes, so after the send there is nothing left to copy.
                    await cache.SaveAsync(ok.Value, extractorName, cancellationToken);
                    await messenger.ReplyWithMediaAsync(
                        message.ChatId,
                        message.MessageId,
                        ok.Value,
                        cancellationToken);
                    metrics.RecordMediaRepost(extractorName);
                    logger.LogInformation(
                        "Reposted {Count} media item(s) from {Url} via {Extractor} into chat {ChatId}",
                        ok.Value.Items.Count, url, extractorName, message.ChatId);
                    return;

                case Result<MediaPayload, ExtractionError>.Ok ok when HasReplyableText(ok.Value):
                    textFallback ??= (ok.Value, extractorName);
                    sawSubstantiveAttempt = true;
                    break;

                case Result<MediaPayload, ExtractionError>.Err err when err.Error is ExtractionError.UnsupportedPlatform:
                    // This extractor doesn't claim the URL; treat it as if CanHandle had returned
                    // false. Silent skip — no ack, no warning.
                    logger.LogDebug(
                        "{Extractor} marked {Url} as unsupported", extractorName, url);
                    break;

                case Result<MediaPayload, ExtractionError>.Err err:
                    logger.LogWarning(
                        "{Extractor} failed for {Url}: {Reason}",
                        extractorName, url, err.Error.Reason);
                    metrics.RecordFailure(extractorName);
                    sawSubstantiveAttempt = true;
                    break;

                case Result<MediaPayload, ExtractionError>.Ok:
                    // Extractor returned nothing usable; try the next candidate.
                    sawSubstantiveAttempt = true;
                    break;
            }
        }

        if (textFallback is { } fallback)
        {
            await cache.SaveAsync(fallback.Payload, fallback.Extractor, cancellationToken);
            await messenger.ReplyWithTextAsync(
                message.ChatId,
                message.MessageId,
                fallback.Payload,
                cancellationToken);
            metrics.RecordTextRepost(fallback.Extractor);
            logger.LogInformation(
                "Reposted text body from {Url} into chat {ChatId}",
                url, message.ChatId);
            return;
        }

        if (!sawSubstantiveAttempt)
        {
            // Every extractor declined the URL — bare http link to a non-media site, basically.
            // Stay silent so the chat isn't flooded with noise for github / news / blog URLs
            // that nobody wanted reposted in the first place.
            metrics.RecordSilentSkip();
            logger.LogDebug("No extractor claimed {Url} — silent skip", url);
            return;
        }

        // An extractor claimed the URL but produced neither media nor a text fallback. Stay
        // silent rather than posting a "couldn't extract" notice — a failed extraction shouldn't
        // add chat noise. The reason was already surfaced in the loop above (a warning for hard
        // failures, debug otherwise).
        logger.LogDebug("Nothing extractable from {Url} — staying silent", url);
    }

    /// <summary>
    /// Replays a stored repost when the URL has been handled before. Returns false on a miss — and
    /// also for the pathological case of a stored payload with nothing left to say, which then falls
    /// through to a normal extraction.
    /// </summary>
    private async Task<bool> TryServeFromCacheAsync(
        IncomingMessage message,
        Uri url,
        CancellationToken cancellationToken)
    {
        var cached = await cache.TryGetAsync(url, cancellationToken);
        if (cached is null)
        {
            return false;
        }

        if (cached.Payload.HasMedia)
        {
            await messenger.ReplyWithMediaAsync(
                message.ChatId,
                message.MessageId,
                cached.Payload,
                cancellationToken);
            metrics.RecordMediaRepost(cached.Extractor);
        }
        else if (HasReplyableText(cached.Payload))
        {
            await messenger.ReplyWithTextAsync(
                message.ChatId,
                message.MessageId,
                cached.Payload,
                cancellationToken);
            metrics.RecordTextRepost(cached.Extractor);
        }
        else
        {
            return false;
        }

        metrics.RecordCacheHit();
        logger.LogInformation(
            "Served {Url} from cache (originally via {Extractor}) into chat {ChatId}",
            url, cached.Extractor, message.ChatId);
        return true;
    }

    private static bool HasReplyableText(MediaPayload payload)
    {
        return !string.IsNullOrWhiteSpace(payload.Description)
            || !string.IsNullOrWhiteSpace(payload.Title);
    }
}
