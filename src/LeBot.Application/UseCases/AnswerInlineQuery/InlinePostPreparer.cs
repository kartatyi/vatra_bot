using System.Collections.Concurrent;
using LeBot.Application.Ports;
using LeBot.Domain.Common;
using LeBot.Domain.Media;
using Microsoft.Extensions.Logging;

namespace LeBot.Application.UseCases.AnswerInlineQuery;

/// <summary>
/// Turns a link into a <see cref="PreparedPost"/>: from the store when it was prepared before, otherwise
/// by fetching it (media cache first, then the extractor chain, as a group repost would) and uploading
/// the media to Telegram. A preparation outlives the inline query that started it — an answer can only
/// wait a few seconds, but the next keystroke's query should find the work done — so it runs on its own
/// clock, and concurrent queries for one link share a single run.
/// </summary>
/// <remarks>
/// Unlike a group repost, nothing here is journalled: there's no chat to attribute the outcome to, and
/// an inline query re-fires on every keystroke, which would flood the dashboard.
/// </remarks>
public sealed class InlinePostPreparer(
    IMediaCache cache,
    IEnumerable<IPlatformExtractor> extractors,
    IInlineMediaUploader uploader,
    IPreparedPostStore store,
    TimeProvider timeProvider,
    ILogger<InlinePostPreparer> logger)
{
    /// <summary>The longest one preparation may run — longer than any answer waits, short enough to free a stuck one.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    private readonly ConcurrentDictionary<string, Lazy<Task<PreparedPost?>>> _inFlight = new(StringComparer.Ordinal);

    /// <summary>
    /// The prepared post for <paramref name="url"/>, or <see langword="null"/> when nothing could be
    /// fetched or uploaded. Joins a preparation already running for the same link.
    /// </summary>
    public Task<PreparedPost?> PrepareAsync(Uri url)
    {
        // Lazy, because GetOrAdd may run its factory twice under a race — only the stored one may start work.
        var run = _inFlight.GetOrAdd(url.AbsoluteUri, _ => new Lazy<Task<PreparedPost?>>(() => RunAsync(url)));
        return run.Value;
    }

    private async Task<PreparedPost?> RunAsync(Uri url)
    {
        using var timeout = new CancellationTokenSource(Timeout, timeProvider);
        try
        {
            return await store.TryGetAsync(url, timeout.Token)
                ?? await FetchAndUploadAsync(url, timeout.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || timeout.IsCancellationRequested)
        {
            // A detached job is its own boundary: nobody may be awaiting it any more, so an escaping
            // exception would only surface as an unobserved task. Log it and report "nothing".
            logger.LogError(ex, "Preparing an inline post for {Url} failed", url);
            return null;
        }
        finally
        {
            _inFlight.TryRemove(url.AbsoluteUri, out _);
        }
    }

    private async Task<PreparedPost?> FetchAndUploadAsync(Uri url, CancellationToken cancellationToken)
    {
        var payload = await FetchAsync(url, cancellationToken);
        if (payload is null)
        {
            return null;
        }

        PreparedPost post;
        if (payload.HasMedia)
        {
            if (await uploader.UploadAsync(payload, cancellationToken) is not { } uploaded)
            {
                return null;
            }

            post = new PreparedPost(uploaded.Kind, uploaded.FileId, payload.Body);
        }
        else
        {
            post = new PreparedPost(Kind: null, FileId: null, payload.Body);
        }

        await store.SaveAsync(url, post, cancellationToken);
        return post;
    }

    /// <summary>The media cache's copy, else the first extractor's media, else the first text; null when there's neither.</summary>
    private async Task<MediaPayload?> FetchAsync(Uri url, CancellationToken cancellationToken)
    {
        if (await cache.TryGetAsync(url, cancellationToken) is { } cached)
        {
            return cached.Payload;
        }

        MediaPayload? textOnly = null;
        foreach (var extractor in extractors.Where(e => e.CanHandle(url)))
        {
            var result = await extractor.ExtractAsync(url, cancellationToken);
            switch (result)
            {
                case Result<MediaPayload, ExtractionError>.Ok ok when ok.Value.HasMedia:
                    // Cache before the upload deletes the downloaded files — same order as a group repost.
                    return await cache.SaveAsync(ok.Value, extractor.GetType().Name, cancellationToken);
                case Result<MediaPayload, ExtractionError>.Ok ok when ok.Value.Body is not null:
                    textOnly ??= ok.Value;
                    break;
                case Result<MediaPayload, ExtractionError>.Err err:
                    logger.LogDebug("{Extractor} could not fetch {Url} inline: {Reason}", extractor.GetType().Name, url, err.Error.Reason);
                    break;
            }
        }

        return textOnly;
    }
}
