using LeBot.Application.Metrics;
using LeBot.Application.Ports;
using LeBot.Application.Telemetry;
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
    IRepostJournal journal,
    TimeProvider timeProvider,
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
            // Not even a candidate — the common case for bare github / news / blog links. The journal
            // skips it on purpose: recording every non-media URL would bury the signal in noise.
            logger.LogDebug("No extractor for URL {Url}", url);
            return;
        }

        // Show "Bot is uploading a video..." in the chat header for the whole life of this
        // method so the user has immediate feedback while extraction + upload run. The
        // indicator is cancelled when ProcessUrlAsync returns (success, failure, or fallback).
        await using var busy = messenger.IndicateBusy(message.ChatId, BusyKind.UploadingVideo);

        // Monotonic start stamp; every journalled event records wall-clock time up to its terminal state.
        var startedAt = timeProvider.GetTimestamp();

        MediaPayload? textFallback = null;
        string? textFallbackExtractor = null;
        var sawSubstantiveAttempt = false;

        foreach (var extractor in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var extractorName = extractor.GetType().Name;
            var result = await extractor.ExtractAsync(url, cancellationToken);
            switch (result)
            {
                case Result<MediaPayload, ExtractionError>.Ok ok when ok.Value.HasMedia:
                    await messenger.ReplyWithMediaAsync(
                        message.ChatId,
                        message.MessageId,
                        ok.Value,
                        cancellationToken);
                    metrics.RecordMediaRepost(extractorName);
                    await journal.RecordMediaRepostAsync(
                        url, extractorName, ok.Value.Items.Count, TotalBytes(ok.Value),
                        timeProvider.GetElapsedTime(startedAt), message.ChatId, cancellationToken);
                    logger.LogInformation(
                        "Reposted {Count} media item(s) from {Url} via {Extractor} into chat {ChatId}",
                        ok.Value.Items.Count, url, extractorName, message.ChatId);
                    return;

                case Result<MediaPayload, ExtractionError>.Ok ok when HasReplyableText(ok.Value):
                    if (textFallback is null)
                    {
                        textFallback = ok.Value;
                        textFallbackExtractor = extractorName;
                    }
                    sawSubstantiveAttempt = true;
                    break;

                case Result<MediaPayload, ExtractionError>.Err err when err.Error is ExtractionError.UnsupportedPlatform:
                    // This extractor doesn't claim the URL; treat it as if CanHandle had returned
                    // false. Silent skip — no ack, no warning, no journal row.
                    logger.LogDebug(
                        "{Extractor} marked {Url} as unsupported", extractorName, url);
                    break;

                case Result<MediaPayload, ExtractionError>.Err err:
                    logger.LogWarning(
                        "{Extractor} failed for {Url}: {Reason}",
                        extractorName, url, err.Error.Reason);
                    metrics.RecordFailure(extractorName);
                    await journal.RecordFailureAsync(
                        url, extractorName, err.Error,
                        timeProvider.GetElapsedTime(startedAt), message.ChatId, cancellationToken);
                    sawSubstantiveAttempt = true;
                    break;

                case Result<MediaPayload, ExtractionError>.Ok:
                    // Extractor claimed the URL but returned an empty payload; record it and try the next.
                    await journal.RecordNothingExtractedAsync(
                        url, extractorName,
                        timeProvider.GetElapsedTime(startedAt), message.ChatId, cancellationToken);
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
            await journal.RecordTextFallbackAsync(
                url, textFallbackExtractor,
                timeProvider.GetElapsedTime(startedAt), message.ChatId, cancellationToken);
            logger.LogInformation(
                "Reposted text body from {Url} into chat {ChatId}",
                url, message.ChatId);
            return;
        }

        if (!sawSubstantiveAttempt)
        {
            // Every candidate returned UnsupportedPlatform — claimed the host pattern but disowned this
            // specific URL. Stay silent so the chat isn't flooded with noise, but journal it: a platform
            // that suddenly stops claiming its own URLs is exactly the kind of breakage the dashboard
            // should surface.
            metrics.RecordSilentSkip();
            await journal.RecordNoExtractorAsync(
                url, timeProvider.GetElapsedTime(startedAt), message.ChatId, cancellationToken);
            logger.LogDebug("No extractor claimed {Url} — silent skip", url);
            return;
        }

        // An extractor claimed the URL but produced neither media nor a text fallback. Stay silent
        // rather than posting a "couldn't extract" notice — the per-extractor outcome (a Failure or
        // NothingExtracted row) was already journalled in the loop above.
        logger.LogDebug("Nothing extractable from {Url} — staying silent", url);
    }

    private static bool HasReplyableText(MediaPayload payload)
    {
        return !string.IsNullOrWhiteSpace(payload.Description)
            || !string.IsNullOrWhiteSpace(payload.Title);
    }

    /// <summary>
    /// Total bytes across all media items, or null when any item's size is unknown — recording a
    /// partial sum as if it were the whole would skew the dashboard's bandwidth figures.
    /// </summary>
    private static long? TotalBytes(MediaPayload payload)
    {
        long total = 0;
        foreach (var item in payload.Items)
        {
            if (item.SizeBytes is not { } size)
            {
                return null;
            }

            total += size;
        }

        return total;
    }
}
