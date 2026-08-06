using System.Net;
using LeBot.Application.Ports;
using LeBot.Domain.Common;
using LeBot.Domain.Media;
using LeBot.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

namespace LeBot.Infrastructure.MediaExtraction.Threads;

/// <summary>
/// Reposts a Threads post as the media it actually contains — every photo of a carousel, the real
/// clip of a video post, the author's own caption — by reading the payload the page ships with
/// itself (see <see cref="ThreadsPostPayload"/>).
///
/// It runs ahead of <c>ThreadsEmbedExtractor</c> and declines whenever it can't describe the post,
/// so a text-only post still falls through to the og:image card Threads renders the body text into.
/// </summary>
internal sealed class ThreadsPostExtractor : IPlatformExtractor
{
    // Telegram albums top out at 10; a Threads carousel holds up to 20.
    private const int MaxAlbumItems = 10;

    // A "1/8" thread is already long for a chat; past that the repost stops being a courtesy.
    private const int MaxContinuationParts = 10;

    private static readonly string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/120.0.0.0 Safari/537.36";

    // Threads only server-renders the payload for requests that look like a browser navigation —
    // a bare User-Agent gets the logged-out shell, which is what made this data look unreachable
    // when ADR 0006 first went looking for it.
    private static readonly (string Name, string Value)[] NavigationHeaders =
    [
        ("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8"),
        ("Accept-Language", "en-US,en;q=0.9"),
        ("Sec-Fetch-Dest", "document"),
        ("Sec-Fetch-Mode", "navigate"),
        ("Sec-Fetch-Site", "none"),
        ("Sec-Fetch-User", "?1"),
        ("Upgrade-Insecure-Requests", "1"),
        ("sec-ch-ua", "\"Chromium\";v=\"120\", \"Not:A-Brand\";v=\"24\""),
        ("sec-ch-ua-platform", "\"Windows\""),
    ];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IBrowserPayloadLoader _browserLoader;
    private readonly YtDlpOptions _options;
    private readonly ILogger<ThreadsPostExtractor> _logger;
    private readonly ResiliencePipeline<HttpResponseMessage> _retryPipeline;

    public ThreadsPostExtractor(
        IHttpClientFactory httpClientFactory,
        IBrowserPayloadLoader browserLoader,
        IOptions<YtDlpOptions> options,
        ILogger<ThreadsPostExtractor> logger)
    {
        _httpClientFactory = httpClientFactory;
        _browserLoader = browserLoader;
        _options = options.Value;
        _logger = logger;

        Directory.CreateDirectory(_options.ResolvedDownloadDirectory);

        _retryPipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .Handle<HttpRequestException>()
                    .Handle<TaskCanceledException>(ex => ex.InnerException is TimeoutException)
                    .HandleResult(static r => IsTransientStatus(r.StatusCode)),
                MaxRetryAttempts = 2,
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromMilliseconds(300),
                MaxDelay = TimeSpan.FromSeconds(5),
                UseJitter = true,
                OnRetry = args =>
                {
                    _logger.LogWarning(
                        "Retrying Threads HTTP call (attempt {Attempt} after {DelayMs}ms): {Reason}",
                        args.AttemptNumber + 1,
                        args.RetryDelay.TotalMilliseconds,
                        args.Outcome.Exception?.Message ?? $"status {args.Outcome.Result?.StatusCode}");
                    return ValueTask.CompletedTask;
                },
            })
            .Build();
    }

    public bool CanHandle(Uri url) => ThreadsUrl.IsPost(url);

    public async Task<Result<MediaPayload, ExtractionError>> ExtractAsync(
        Uri url,
        CancellationToken cancellationToken)
    {
        try
        {
            var post = await LoadPostAsync(url, cancellationToken);
            if (post is null || post.Media.Count == 0)
            {
                // Either the page never described the post, or it's text-only. Both are the embed
                // extractor's case: it renders the body as Threads' own card.
                _logger.LogDebug("No post media described for {Url}; leaving it to the og:image card", url);
                return Result<MediaPayload, ExtractionError>.Failure(new ExtractionError.UnsupportedPlatform(url));
            }

            var items = await DownloadAllAsync(post.Media, cancellationToken);
            if (items.Count == 0)
            {
                _logger.LogWarning(
                    "Threads post {Url} listed {Count} media but downloaded zero", url, post.Media.Count);
                return Result<MediaPayload, ExtractionError>.Failure(new ExtractionError.UnsupportedPlatform(url));
            }

            var followUps = await DownloadContinuationAsync(post, cancellationToken);
            if (followUps.Count > 0)
            {
                _logger.LogInformation(
                    "Threads post {Url} continues in {Count} more part(s)", url, followUps.Count);
            }

            return Result<MediaPayload, ExtractionError>.Success(
                new MediaPayload(url, Title: null, Author: post.Author, Items: items, Description: post.Caption)
                {
                    FollowUps = followUps,
                });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Threads HTTP failure for {Url}", url);
            return Result<MediaPayload, ExtractionError>.Failure(new ExtractionError.NetworkFailure(url, ex.Message));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Unexpected error extracting Threads {Url}", url);
            return Result<MediaPayload, ExtractionError>.Failure(new ExtractionError.ToolFailure(ex.Message));
        }
    }

    /// <summary>
    /// Fetches the page and reads the post out of it. Meta sometimes answers with the logged-out
    /// shell (or bounces the request to the home feed) instead of the payload, so a miss escalates:
    /// plain fetch, then the headless browser, then give up.
    /// </summary>
    private async Task<ThreadsPost?> LoadPostAsync(Uri url, CancellationToken cancellationToken)
    {
        var (finalUrl, html) = await FetchPageAsync(url, cancellationToken);

        // A /share/ shortlink only names its post after the redirect, so the final URL decides.
        var shortcode = ThreadsUrl.Shortcode(finalUrl) ?? ThreadsUrl.Shortcode(url);
        if (shortcode is null)
        {
            // Deleted or private posts land on the home feed or ?error=invalid_post.
            _logger.LogInformation("Threads did not resolve {Url} to a post page", url);
            return null;
        }

        var post = html is null ? null : ThreadsPostPayload.FromHtml(html, shortcode);
        if (post is not null)
        {
            return post;
        }

        _logger.LogInformation(
            "Threads served no payload for {Shortcode} over HTTP; retrying in a headless browser", shortcode);

        var payload = await _browserLoader.LoadPostPayloadAsync(finalUrl, shortcode, cancellationToken);
        return payload is null ? null : ThreadsPostPayload.FromJson(payload, shortcode);
    }

    private async Task<(Uri FinalUrl, string? Html)> FetchPageAsync(Uri url, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(nameof(ThreadsPostExtractor));

        using var response = await _retryPipeline.ExecuteAsync(async token =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.Clear();
            request.Headers.UserAgent.TryParseAdd(BrowserUserAgent);
            foreach (var (name, value) in NavigationHeaders)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }

            return await client.SendAsync(request, token);
        }, cancellationToken);

        // RequestUri is the last hop the client followed; null only if the message was disposed.
        var finalUrl = response.RequestMessage?.RequestUri ?? url;

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Threads page returned HTTP {Status} for {Url}", (int)response.StatusCode, url);
            return (finalUrl, null);
        }

        return (finalUrl, await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private async Task<List<MediaItem>> DownloadAllAsync(
        IReadOnlyList<ThreadsMediaSource> sources,
        CancellationToken cancellationToken)
    {
        var maxBytes = (long)_options.MaxFileSizeMb * 1024 * 1024;
        var client = _httpClientFactory.CreateClient(nameof(ThreadsPostExtractor));
        var items = new List<MediaItem>(sources.Count);

        foreach (var source in sources)
        {
            if (items.Count >= MaxAlbumItems)
            {
                _logger.LogInformation(
                    "Threads post carries {Total} media; sending the first {Sent}", sources.Count, MaxAlbumItems);
                break;
            }

            var item = await DownloadAsync(client, source, maxBytes, cancellationToken);
            if (item is not null)
            {
                items.Add(item);
            }
        }

        return items;
    }

    /// <summary>
    /// Turns the author's chained parts into segments the sender can post after the main reply. A
    /// part whose media won't download still ships its text — losing the words because a photo 404'd
    /// would be the worse trade.
    /// </summary>
    private async Task<List<PostSegment>> DownloadContinuationAsync(
        ThreadsPost post,
        CancellationToken cancellationToken)
    {
        var segments = new List<PostSegment>();

        foreach (var part in post.Continuation)
        {
            if (segments.Count >= MaxContinuationParts)
            {
                _logger.LogInformation(
                    "Threads chain has {Total} parts; keeping the first {Kept}",
                    post.Continuation.Count, MaxContinuationParts);
                break;
            }

            var items = part.Media.Count > 0
                ? await DownloadAllAsync(part.Media, cancellationToken)
                : [];

            var segment = new PostSegment(part.Text, items);
            if (segment.HasContent)
            {
                segments.Add(segment);
            }
        }

        return segments;
    }

    private async Task<MediaItem?> DownloadAsync(
        HttpClient client,
        ThreadsMediaSource source,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _retryPipeline.ExecuteAsync(async token =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, source.Url);
                request.Headers.UserAgent.ParseAdd(BrowserUserAgent);
                return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            }, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Threads media fetch returned HTTP {Status}", (int)response.StatusCode);
                return null;
            }

            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength is > 0 && contentLength > maxBytes)
            {
                _logger.LogInformation(
                    "Skipping Threads media: predicted size {SizeMb}MB exceeds limit {LimitMb}MB",
                    contentLength / (1024 * 1024), _options.MaxFileSizeMb);
                return null;
            }

            var contentType = response.Content.Headers.ContentType?.MediaType;
            var extension = ExtensionFor(source.Kind, contentType, source.Url);
            var filePath = Path.Combine(_options.ResolvedDownloadDirectory, $"threads_{Guid.NewGuid():N}{extension}");

            // Cap while streaming, not after: a chunked CDN response carries no Content-Length, so
            // the predictive guard above can't fire and an oversized body would otherwise hit the
            // disk in full before we noticed.
            bool withinCap;
            await using (var fileStream = File.Create(filePath))
            await using (var httpStream = await response.Content.ReadAsStreamAsync(cancellationToken))
            {
                withinCap = await CopyWithinCapAsync(httpStream, fileStream, maxBytes, cancellationToken);
            }

            if (!withinCap)
            {
                _logger.LogInformation(
                    "Discarding Threads media {Path}: stream exceeded limit {LimitMb}MB",
                    filePath, _options.MaxFileSizeMb);
                BestEffortDelete(filePath);
                return null;
            }

            var fileInfo = new FileInfo(filePath);

            return new MediaItem(
                FilePath: filePath,
                Kind: source.Kind,
                MimeType: contentType ?? DefaultMimeType(source.Kind),
                SizeBytes: fileInfo.Length,
                DurationSeconds: null);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Threads media download failed for one item");
            return null;
        }
    }

    private static string ExtensionFor(MediaKind kind, string? contentType, string sourceUrl)
    {
        var fromContentType = contentType?.ToLowerInvariant() switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/webp" => ".webp",
            "image/heic" => ".heic",
            "video/mp4" => ".mp4",
            _ => null,
        };
        if (fromContentType is not null)
        {
            return fromContentType;
        }

        var ext = Path.GetExtension(sourceUrl.Split('?', 2)[0]).ToLowerInvariant();
        if (ext is ".jpg" or ".jpeg" or ".png" or ".webp" or ".heic" or ".mp4")
        {
            return ext;
        }

        return kind == MediaKind.Video ? ".mp4" : ".jpg";
    }

    private static string DefaultMimeType(MediaKind kind) =>
        kind == MediaKind.Video ? "video/mp4" : "image/jpeg";

    private static async Task<bool> CopyWithinCapAsync(
        Stream source,
        Stream destination,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                return false;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return true;
    }

    private static bool IsTransientStatus(HttpStatusCode status) =>
        status == HttpStatusCode.RequestTimeout
        || status == HttpStatusCode.TooManyRequests
        || ((int)status >= 500 && (int)status < 600);

    private static void BestEffortDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
