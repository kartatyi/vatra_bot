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

namespace LeBot.Infrastructure.MediaExtraction.Instagram;

/// <summary>
/// Extracts photos and videos from Instagram <c>/p/</c> and <c>/tv/</c> posts (including image
/// carousels) via Instagram's private web API — the same <c>api/v1/media/{id}/info/</c> endpoint the
/// website's own client calls. We go to the API rather than yt-dlp because yt-dlp's Instagram
/// extractor only yields video formats: a photo post comes back with zero downloadable formats and
/// is dropped. Scraping the public embed HTML no longer works either — current Instagram renders
/// carousel images client-side, so the markup carries no image URLs. The API needs a logged-in
/// session, sourced from <see cref="IInstagramCookieProvider"/>; without one this extractor returns
/// an empty payload and lets the chain fall through.
/// </summary>
internal sealed class InstagramApiExtractor : IPlatformExtractor
{
    // The public web app id Instagram's own site sends; without it api/v1 answers 403.
    private const string WebAppId = "936619743392459";

    // Telegram albums top out at 10 items; an Instagram carousel can hold up to 20.
    private const int MaxAlbumItems = 10;

    private static readonly string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/120.0.0.0 Safari/537.36";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IInstagramCookieProvider _cookieProvider;
    private readonly YtDlpOptions _options;
    private readonly ILogger<InstagramApiExtractor> _logger;
    private readonly ResiliencePipeline<HttpResponseMessage> _retryPipeline;

    public InstagramApiExtractor(
        IHttpClientFactory httpClientFactory,
        IInstagramCookieProvider cookieProvider,
        IOptions<YtDlpOptions> options,
        ILogger<InstagramApiExtractor> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cookieProvider = cookieProvider;
        _options = options.Value;
        _logger = logger;

        Directory.CreateDirectory(_options.DownloadDirectory);

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
                        "Retrying Instagram API HTTP call (attempt {Attempt} after {DelayMs}ms): {Reason}",
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

        // /p/ and /tv/ posts can be photos, carousels, or video. Reels (/reel/) are always video and
        // yt-dlp already handles them well, so they're deliberately left to it.
        return host.Equals("instagram.com", StringComparison.OrdinalIgnoreCase)
            && Shortcode(url) is not null;
    }

    public async Task<Result<MediaPayload, ExtractionError>> ExtractAsync(
        Uri url,
        CancellationToken cancellationToken)
    {
        var shortcode = Shortcode(url);
        if (shortcode is null)
        {
            return Result<MediaPayload, ExtractionError>.Failure(new ExtractionError.UnsupportedPlatform(url));
        }

        var mediaId = InstagramMediaId.FromShortcode(shortcode);
        if (mediaId is null)
        {
            _logger.LogWarning("Could not decode Instagram shortcode {Shortcode} from {Url}", shortcode, url);
            return Result<MediaPayload, ExtractionError>.Failure(new ExtractionError.UnsupportedPlatform(url));
        }

        var cookies = await _cookieProvider.GetAsync(forceRefresh: false, cancellationToken);
        if (cookies is null)
        {
            // No session → the private API will 403. Surface an empty payload (not a failure) so the
            // handler can fall through; the log tells the operator how to switch authentication on.
            _logger.LogWarning(
                "No Instagram session available — set YtDlp:CookiesFromBrowser to a browser logged into Instagram. Skipping {Url}",
                url);
            return Result<MediaPayload, ExtractionError>.Success(
                new MediaPayload(url, Title: null, Author: null, Items: []));
        }

        try
        {
            var (status, json) = await FetchMediaInfoAsync(mediaId, cookies, cancellationToken);

            // A stale session shows up as 401/403. Refresh the cookies once and retry before giving up.
            if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                _logger.LogInformation(
                    "Instagram API returned {Status} for {Url}; refreshing session and retrying", (int)status, url);
                var refreshed = await _cookieProvider.GetAsync(forceRefresh: true, cancellationToken);
                if (refreshed is not null)
                {
                    (status, json) = await FetchMediaInfoAsync(mediaId, refreshed, cancellationToken);
                }
            }

            if (status != HttpStatusCode.OK || json is null)
            {
                _logger.LogWarning("Instagram API returned HTTP {Status} for {Url}", (int)status, url);
                return Result<MediaPayload, ExtractionError>.Failure(
                    new ExtractionError.ContentUnavailable(url, $"HTTP {(int)status}"));
            }

            return await BuildPayloadAsync(url, json, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Instagram API HTTP failure for {Url}", url);
            return Result<MediaPayload, ExtractionError>.Failure(new ExtractionError.NetworkFailure(url, ex.Message));
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Instagram API returned unparseable JSON for {Url}", url);
            return Result<MediaPayload, ExtractionError>.Failure(new ExtractionError.ToolFailure(ex.Message));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Unexpected error extracting Instagram {Url}", url);
            return Result<MediaPayload, ExtractionError>.Failure(new ExtractionError.ToolFailure(ex.Message));
        }
    }

    private async Task<(HttpStatusCode Status, string? Body)> FetchMediaInfoAsync(
        string mediaId,
        InstagramCookies cookies,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(nameof(InstagramApiExtractor));
        var endpoint = new Uri($"https://www.instagram.com/api/v1/media/{mediaId}/info/");

        using var response = await _retryPipeline.ExecuteAsync(async token =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.UserAgent.ParseAdd(BrowserUserAgent);
            request.Headers.TryAddWithoutValidation("X-IG-App-ID", WebAppId);
            request.Headers.TryAddWithoutValidation("Cookie", cookies.ToHeaderValue());
            return await client.SendAsync(request, token);
        }, cancellationToken);

        if (response.StatusCode != HttpStatusCode.OK)
        {
            return (response.StatusCode, null);
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return (HttpStatusCode.OK, body);
    }

    private async Task<Result<MediaPayload, ExtractionError>> BuildPayloadAsync(
        Uri url,
        string json,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array
            || items.GetArrayLength() == 0)
        {
            _logger.LogInformation("Instagram API returned no items for {Url}", url);
            return Result<MediaPayload, ExtractionError>.Success(
                new MediaPayload(url, Title: null, Author: null, Items: []));
        }

        var item = items[0];
        var author = TryGetString(item, "user", "username");
        var caption = TryGetString(item, "caption", "text");

        var sources = CollectMediaSources(item);
        if (sources.Count == 0)
        {
            _logger.LogInformation("Instagram post {Url} carried no downloadable media", url);
            return Result<MediaPayload, ExtractionError>.Success(
                new MediaPayload(url, Title: null, Author: author, Items: [], Description: caption));
        }

        var maxBytes = (long)_options.MaxFileSizeMb * 1024 * 1024;
        var client = _httpClientFactory.CreateClient(nameof(InstagramApiExtractor));
        var mediaItems = new List<MediaItem>(sources.Count);

        foreach (var source in sources)
        {
            if (mediaItems.Count >= MaxAlbumItems)
            {
                break;
            }

            var downloaded = await DownloadAsync(client, source, maxBytes, cancellationToken);
            if (downloaded is not null)
            {
                mediaItems.Add(downloaded);
            }
        }

        if (mediaItems.Count == 0)
        {
            _logger.LogWarning("Instagram post {Url} listed {Count} media but downloaded zero", url, sources.Count);
        }

        return Result<MediaPayload, ExtractionError>.Success(
            new MediaPayload(url, Title: null, Author: author, Items: mediaItems, Description: caption));
    }

    private static List<MediaSource> CollectMediaSources(JsonElement item)
    {
        var sources = new List<MediaSource>();

        if (item.TryGetProperty("carousel_media", out var carousel)
            && carousel.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in carousel.EnumerateArray())
            {
                AddSource(child, sources);
            }
        }
        else
        {
            AddSource(item, sources);
        }

        return sources;
    }

    private static void AddSource(JsonElement node, List<MediaSource> sources)
    {
        // Prefer a real video rendition when the node carries one; otherwise treat it as an image.
        // Keying off the presence of video_versions (rather than the media_type tag) handles image
        // and video carousel children uniformly and survives a missing or unknown media_type.
        if (TryBestVideo(node, out var videoUrl))
        {
            sources.Add(new MediaSource(videoUrl, MediaKind.Video));
            return;
        }

        if (TryBestImage(node, out var imageUrl))
        {
            sources.Add(new MediaSource(imageUrl, MediaKind.Photo));
        }
    }

    private static bool TryBestVideo(JsonElement node, out string url)
    {
        url = string.Empty;

        // video_versions is ordered highest-quality first.
        if (node.TryGetProperty("video_versions", out var versions)
            && versions.ValueKind == JsonValueKind.Array
            && versions.GetArrayLength() > 0
            && versions[0].TryGetProperty("url", out var u)
            && u.GetString() is { Length: > 0 } best)
        {
            url = best;
            return true;
        }

        return false;
    }

    private static bool TryBestImage(JsonElement node, out string url)
    {
        url = string.Empty;

        // image_versions2.candidates is ordered highest-resolution first.
        if (node.TryGetProperty("image_versions2", out var versions)
            && versions.TryGetProperty("candidates", out var candidates)
            && candidates.ValueKind == JsonValueKind.Array
            && candidates.GetArrayLength() > 0
            && candidates[0].TryGetProperty("url", out var u)
            && u.GetString() is { Length: > 0 } best)
        {
            url = best;
            return true;
        }

        return false;
    }

    private async Task<MediaItem?> DownloadAsync(
        HttpClient client,
        MediaSource source,
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
                _logger.LogWarning("Instagram media fetch returned HTTP {Status}", (int)response.StatusCode);
                return null;
            }

            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength is > 0 && contentLength > maxBytes)
            {
                _logger.LogInformation(
                    "Skipping Instagram media: predicted size {SizeMb}MB exceeds limit {LimitMb}MB",
                    contentLength / (1024 * 1024), _options.MaxFileSizeMb);
                return null;
            }

            var contentType = response.Content.Headers.ContentType?.MediaType;
            var extension = ExtensionFor(source.Kind, contentType, source.Url);
            var filePath = Path.Combine(_options.DownloadDirectory, $"ig_{Guid.NewGuid():N}{extension}");

            // Cap while streaming, not after: a chunked response carries no Content-Length, so the
            // predictive guard above can't fire and an oversized body would otherwise hit the disk
            // in full before we noticed.
            bool withinCap;
            await using (var fileStream = File.Create(filePath))
            await using (var httpStream = await response.Content.ReadAsStreamAsync(cancellationToken))
            {
                withinCap = await CopyWithinCapAsync(httpStream, fileStream, maxBytes, cancellationToken);
            }

            if (!withinCap)
            {
                _logger.LogInformation(
                    "Discarding Instagram media {Path}: stream exceeded limit {LimitMb}MB",
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
            _logger.LogWarning(ex, "Instagram media download failed for one item");
            return null;
        }
    }

    private static string? Shortcode(Uri url)
    {
        var segments = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2
            && (segments[0].Equals("p", StringComparison.OrdinalIgnoreCase)
                || segments[0].Equals("tv", StringComparison.OrdinalIgnoreCase))
            ? segments[1]
            : null;
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

    private static string? TryGetString(JsonElement parent, string objectName, string property) =>
        parent.TryGetProperty(objectName, out var child)
            && child.ValueKind == JsonValueKind.Object
            && child.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

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
}
