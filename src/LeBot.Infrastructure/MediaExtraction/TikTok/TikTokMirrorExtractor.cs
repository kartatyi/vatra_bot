using System.Net;
using System.Text.Json;
using LeBot.Application.Ports;
using LeBot.Domain.Common;
using LeBot.Domain.Media;
using LeBot.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

namespace LeBot.Infrastructure.MediaExtraction.TikTok;

/// <summary>
/// Last-resort TikTok extractor: asks a public mirror API for the post's direct media URL and
/// downloads it over plain HTTP. It exists because TikTok's anti-bot challenge periodically breaks
/// yt-dlp's TikTok extractor outright — every URL fails with "Unexpected response from webpage
/// request" and no version of the tool helps until upstream ships a fix (see ADR 0008).
/// <para>
/// Registered <em>after</em> <c>YtDlpPlatformExtractor</c> so it is genuinely a fallback: while
/// yt-dlp works it wins the chain and this class never runs. Handles both video posts and photo
/// slideshows, which TikTok serves from the same URL shape.
/// </para>
/// </summary>
internal sealed class TikTokMirrorExtractor : IPlatformExtractor
{
    // Telegram albums top out at 10 items; a TikTok slideshow can carry more.
    private const int MaxAlbumItems = 10;

    private static readonly string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/120.0.0.0 Safari/537.36";

    // Every host TikTok hands out: the canonical site, the mobile site, and the two short-link
    // domains the share sheet produces (vm. for the app, vt. for the web share button).
    private static readonly string[] TikTokHosts =
    [
        "tiktok.com",
        "vm.tiktok.com",
        "vt.tiktok.com",
        "m.tiktok.com",
    ];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TikTokOptions _options;
    private readonly YtDlpOptions _downloadOptions;
    private readonly ILogger<TikTokMirrorExtractor> _logger;
    private readonly ResiliencePipeline<HttpResponseMessage> _retryPipeline;

    public TikTokMirrorExtractor(
        IHttpClientFactory httpClientFactory,
        IOptions<TikTokOptions> options,
        IOptions<YtDlpOptions> downloadOptions,
        ILogger<TikTokMirrorExtractor> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _downloadOptions = downloadOptions.Value;
        _logger = logger;

        Directory.CreateDirectory(_downloadOptions.ResolvedDownloadDirectory);

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
                        "Retrying TikTok mirror HTTP call (attempt {Attempt} after {DelayMs}ms): {Reason}",
                        args.AttemptNumber + 1,
                        args.RetryDelay.TotalMilliseconds,
                        args.Outcome.Exception?.Message ?? $"status {args.Outcome.Result?.StatusCode}");
                    return ValueTask.CompletedTask;
                },
            })
            .Build();
    }

    public bool CanHandle(Uri url)
    {
        var host = url.Host;
        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            host = host[4..];
        }

        return TikTokHosts.Contains(host, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<Result<MediaPayload, ExtractionError>> ExtractAsync(
        Uri url,
        CancellationToken cancellationToken)
    {
        if (!CanHandle(url))
        {
            return Result<MediaPayload, ExtractionError>.Failure(new ExtractionError.UnsupportedPlatform(url));
        }

        if (!_options.MirrorFallbackEnabled || _options.MirrorEndpoints.Count == 0)
        {
            // Disowning the URL (rather than failing) keeps the switched-off path silent: the handler
            // treats it exactly as if CanHandle had said no — no warning, no journal row.
            return Result<MediaPayload, ExtractionError>.Failure(new ExtractionError.UnsupportedPlatform(url));
        }

        ExtractionError? lastError = null;

        foreach (var endpoint in _options.MirrorEndpoints)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var attempt = await TryEndpointAsync(endpoint, url, cancellationToken);
            if (attempt.IsSuccess)
            {
                return attempt.Result;
            }

            lastError = attempt.Error;
            _logger.LogWarning(
                "TikTok mirror {Endpoint} could not serve {Url}: {Reason}",
                endpoint, url, attempt.Error?.Reason);
        }

        return Result<MediaPayload, ExtractionError>.Failure(
            lastError ?? new ExtractionError.ContentUnavailable(url, "no mirror returned media"));
    }

    private async Task<EndpointAttempt> TryEndpointAsync(
        string endpoint,
        Uri url,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            var (status, json) = await FetchAsync(endpoint, url, timeout.Token);
            if (status != HttpStatusCode.OK || json is null)
            {
                return EndpointAttempt.Failed(new ExtractionError.ContentUnavailable(url, $"HTTP {(int)status}"));
            }

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            // tikwm answers 200 with a non-zero code for "video not found", private posts, and its
            // own rate limit — the HTTP status alone never tells us the request failed.
            if (!root.TryGetProperty("code", out var code)
                || code.ValueKind != JsonValueKind.Number
                || code.GetInt32() != 0)
            {
                var message = root.TryGetProperty("msg", out var msg) && msg.ValueKind == JsonValueKind.String
                    ? msg.GetString() ?? "mirror rejected the request"
                    : "mirror rejected the request";
                return EndpointAttempt.Failed(new ExtractionError.ContentUnavailable(url, message));
            }

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                return EndpointAttempt.Failed(new ExtractionError.ContentUnavailable(url, "mirror returned no data"));
            }

            var payload = await BuildPayloadAsync(endpoint, url, data, timeout.Token);

            // A mirror that answers "ok" but hands back neither media nor a caption told us nothing.
            // Report it as a failure so the next mirror still gets its turn instead of the chain
            // ending on an empty success.
            if (!payload.HasMedia && string.IsNullOrWhiteSpace(payload.Description))
            {
                return EndpointAttempt.Failed(new ExtractionError.ContentUnavailable(url, "mirror returned no media"));
            }

            return EndpointAttempt.Succeeded(Result<MediaPayload, ExtractionError>.Success(payload));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our own per-endpoint timeout fired, not a shutdown — try the next mirror.
            return EndpointAttempt.Failed(
                new ExtractionError.NetworkFailure(url, $"mirror timed out after {_options.TimeoutSeconds}s"));
        }
        catch (HttpRequestException ex)
        {
            return EndpointAttempt.Failed(new ExtractionError.NetworkFailure(url, ex.Message));
        }
        catch (JsonException ex)
        {
            return EndpointAttempt.Failed(new ExtractionError.ToolFailure(ex.Message));
        }
    }

    private async Task<MediaPayload> BuildPayloadAsync(
        string endpoint,
        Uri url,
        JsonElement data,
        CancellationToken cancellationToken)
    {
        var caption = TryGetString(data, "title");
        var author = TryGetString(data, "author", "nickname") ?? TryGetString(data, "author", "unique_id");
        var duration = data.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
            ? d.GetInt32()
            : (int?)null;

        var sources = CollectSources(data);
        if (sources.Count == 0)
        {
            _logger.LogInformation("TikTok mirror returned no playable media for {Url}", url);
            return new MediaPayload(url, Title: null, Author: author, Items: [], Description: caption);
        }

        var maxBytes = (long)_downloadOptions.MaxFileSizeMb * 1024 * 1024;
        var client = _httpClientFactory.CreateClient(nameof(TikTokMirrorExtractor));
        var items = new List<MediaItem>(sources.Count);

        foreach (var source in sources)
        {
            if (items.Count >= MaxAlbumItems)
            {
                break;
            }

            var absolute = Absolutise(endpoint, source.Url);
            if (absolute is null)
            {
                continue;
            }

            var downloaded = await DownloadAsync(
                client,
                absolute,
                source.Kind,
                source.Kind == MediaKind.Video ? duration : null,
                maxBytes,
                cancellationToken);
            if (downloaded is not null)
            {
                items.Add(downloaded);
            }
        }

        if (items.Count == 0)
        {
            _logger.LogWarning(
                "TikTok mirror listed {Count} media for {Url} but downloaded zero", sources.Count, url);
        }

        return new MediaPayload(url, Title: null, Author: author, Items: items, Description: caption);
    }

    /// <summary>
    /// Photo slideshows come back as an <c>images</c> array and carry no video; everything else is a
    /// single clip. <c>hdplay</c> is preferred when present but is routinely blank on the free tier,
    /// so <c>play</c> — the watermark-free standard rendition — is the real workhorse.
    /// </summary>
    private static List<MediaSource> CollectSources(JsonElement data)
    {
        var sources = new List<MediaSource>();

        if (data.TryGetProperty("images", out var images)
            && images.ValueKind == JsonValueKind.Array
            && images.GetArrayLength() > 0)
        {
            foreach (var image in images.EnumerateArray())
            {
                if (image.ValueKind == JsonValueKind.String && image.GetString() is { Length: > 0 } imageUrl)
                {
                    sources.Add(new MediaSource(imageUrl, MediaKind.Photo));
                }
            }

            return sources;
        }

        var video = FirstNonEmpty(data, "hdplay", "play", "wmplay");
        if (video is not null)
        {
            sources.Add(new MediaSource(video, MediaKind.Video));
        }

        return sources;
    }

    private async Task<(HttpStatusCode Status, string? Body)> FetchAsync(
        string endpoint,
        Uri url,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(nameof(TikTokMirrorExtractor));
        var separator = endpoint.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        var requestUri = new Uri($"{endpoint}{separator}url={Uri.EscapeDataString(url.AbsoluteUri)}&hd=1");

        using var response = await _retryPipeline.ExecuteAsync(async token =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.UserAgent.ParseAdd(BrowserUserAgent);
            return await client.SendAsync(request, token);
        }, cancellationToken);

        if (response.StatusCode != HttpStatusCode.OK)
        {
            return (response.StatusCode, null);
        }

        return (HttpStatusCode.OK, await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private async Task<MediaItem?> DownloadAsync(
        HttpClient client,
        Uri source,
        MediaKind kind,
        int? durationSeconds,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _retryPipeline.ExecuteAsync(async token =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, source);
                request.Headers.UserAgent.ParseAdd(BrowserUserAgent);
                return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            }, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("TikTok media fetch returned HTTP {Status}", (int)response.StatusCode);
                return null;
            }

            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength is > 0 && contentLength > maxBytes)
            {
                _logger.LogInformation(
                    "Skipping TikTok media: predicted size {SizeMb}MB exceeds limit {LimitMb}MB",
                    contentLength / (1024 * 1024), _downloadOptions.MaxFileSizeMb);
                return null;
            }

            var contentType = response.Content.Headers.ContentType?.MediaType;
            var extension = ExtensionFor(kind, contentType, source.AbsoluteUri);
            var filePath = Path.Combine(
                _downloadOptions.ResolvedDownloadDirectory, $"tt_{Guid.NewGuid():N}{extension}");

            // Cap while streaming, not after: the CDN answers chunked for some renditions, so the
            // predictive guard above can't fire and an oversized body would land on disk in full.
            bool withinCap;
            await using (var fileStream = File.Create(filePath))
            await using (var httpStream = await response.Content.ReadAsStreamAsync(cancellationToken))
            {
                withinCap = await CopyWithinCapAsync(httpStream, fileStream, maxBytes, cancellationToken);
            }

            if (!withinCap)
            {
                _logger.LogInformation(
                    "Discarding TikTok media {Path}: stream exceeded limit {LimitMb}MB",
                    filePath, _downloadOptions.MaxFileSizeMb);
                BestEffortDelete(filePath);
                return null;
            }

            return new MediaItem(
                FilePath: filePath,
                Kind: kind,
                MimeType: contentType ?? DefaultMimeType(kind),
                SizeBytes: new FileInfo(filePath).Length,
                DurationSeconds: durationSeconds);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "TikTok media download failed for one item");
            return null;
        }
    }

    /// <summary>
    /// Mirrors sometimes answer with a host-relative path (<c>/video/tos/…</c>) that only resolves
    /// against the mirror's own origin. Returns null for anything that still isn't a usable http(s)
    /// URL, so a malformed entry is skipped instead of throwing mid-download.
    /// <para>
    /// The scheme check has to gate the absolute branch rather than short-circuit after it: on Unix
    /// a leading-slash path parses as an absolute <c>file:</c> URI, so testing "is it absolute" alone
    /// would send every host-relative path down the wrong branch and silently drop the media.
    /// </para>
    /// </summary>
    private static Uri? Absolutise(string endpoint, string candidate)
    {
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var absolute)
            && absolute.Scheme is "http" or "https")
        {
            return absolute;
        }

        return Uri.TryCreate(endpoint, UriKind.Absolute, out var baseUri)
            && Uri.TryCreate(baseUri, candidate, out var combined)
            && combined.Scheme is "http" or "https"
            ? combined
            : null;
    }

    private static string? FirstNonEmpty(JsonElement data, params string[] properties)
    {
        foreach (var property in properties)
        {
            if (data.TryGetProperty(property, out var value)
                && value.ValueKind == JsonValueKind.String
                && value.GetString() is { Length: > 0 } text)
            {
                return text;
            }
        }

        return null;
    }

    private static string? TryGetString(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? TryGetString(JsonElement parent, string objectName, string property) =>
        parent.TryGetProperty(objectName, out var child)
            && child.ValueKind == JsonValueKind.Object
            ? TryGetString(child, property)
            : null;

    private static string ExtensionFor(MediaKind kind, string? contentType, string sourceUrl)
    {
        var fromContentType = contentType?.ToLowerInvariant() switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/webp" => ".webp",
            "video/mp4" => ".mp4",
            _ => null,
        };
        if (fromContentType is not null)
        {
            return fromContentType;
        }

        var ext = Path.GetExtension(sourceUrl.Split('?', 2)[0]).ToLowerInvariant();
        if (ext is ".jpg" or ".jpeg" or ".png" or ".webp" or ".mp4")
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

    private readonly record struct MediaSource(string Url, MediaKind Kind);

    /// <summary>
    /// One mirror's outcome: either a payload to return, or the error to carry to the next mirror.
    /// </summary>
    private readonly record struct EndpointAttempt(
        bool IsSuccess,
        Result<MediaPayload, ExtractionError> Result,
        ExtractionError? Error)
    {
        public static EndpointAttempt Succeeded(Result<MediaPayload, ExtractionError> result) =>
            new(true, result, null);

        public static EndpointAttempt Failed(ExtractionError error) =>
            new(false, Result<MediaPayload, ExtractionError>.Failure(error), error);
    }
}
