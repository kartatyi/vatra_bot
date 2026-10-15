using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Xml;
using LeBot.Application.Ports;
using LeBot.Domain.Common;
using LeBot.Domain.Media;
using LeBot.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

namespace LeBot.Infrastructure.MediaExtraction.Steam;

/// <summary>
/// Reposts a Steam store page as the game itself: its lead trailer and a few screenshots in one album,
/// under a caption with the name, price, release date, genres and review verdict. yt-dlp claims Steam
/// too, but knows nothing of prices or screenshots and takes every trailer on the page at full
/// resolution — a page with three trailers became a two-video album whose upload the connection
/// dropped. The store's JSON API has the facts, and a trailer's DASH manifest tells, before a byte of
/// it is downloaded, which rendition fits.
/// <para>
/// The trailer and the screenshots are best-effort; the caption is not. With no media to be had the
/// caption still goes out, as a text reply.
/// </para>
/// </summary>
internal sealed class SteamStoreExtractor : IPlatformExtractor
{
    // A trailer and three screenshots, or four screenshots: a chat post, not the whole store page.
    private const int MaxAlbumItems = 4;

    // Taller buys nothing in an inline player on a phone, and costs twice the bytes per step up.
    private const int MaxTrailerHeight = 720;

    // Telegram's ceiling for a photo, well above any Steam screenshot.
    private const long MaxPhotoBytes = 10L * 1024 * 1024;

    // The album goes up as one multipart request, and no album over the per-file ceiling has ever
    // made it through, so the whole album — not each file — is held under it, with room for framing.
    private const long RequestFramingBytes = 1024 * 1024;

    private static readonly string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/120.0.0.0 Safari/537.36";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISteamTrailerDownloader _trailerDownloader;
    private readonly SteamOptions _options;
    private readonly YtDlpOptions _downloadOptions;
    private readonly ILogger<SteamStoreExtractor> _logger;
    private readonly ResiliencePipeline<HttpResponseMessage> _retryPipeline;

    public SteamStoreExtractor(
        IHttpClientFactory httpClientFactory,
        ISteamTrailerDownloader trailerDownloader,
        IOptions<SteamOptions> options,
        IOptions<YtDlpOptions> downloadOptions,
        ILogger<SteamStoreExtractor> logger)
    {
        _httpClientFactory = httpClientFactory;
        _trailerDownloader = trailerDownloader;
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
                        "Retrying Steam HTTP call (attempt {Attempt} after {DelayMs}ms): {Reason}",
                        args.AttemptNumber + 1,
                        args.RetryDelay.TotalMilliseconds,
                        args.Outcome.Exception?.Message ?? $"status {args.Outcome.Result?.StatusCode}");
                    return ValueTask.CompletedTask;
                },
            })
            .Build();
    }

    public bool CanHandle(Uri url) => SteamAppUrl.TryGetAppId(url) is not null;

    public async Task<Result<MediaPayload, ExtractionError>> ExtractAsync(
        Uri url,
        CancellationToken cancellationToken)
    {
        if (SteamAppUrl.TryGetAppId(url) is not { } appId)
        {
            return Result<MediaPayload, ExtractionError>.Failure(new ExtractionError.UnsupportedPlatform(url));
        }

        var client = _httpClientFactory.CreateClient(nameof(SteamStoreExtractor));

        try
        {
            var (status, detailsJson) = await GetStringAsync(client, AppDetailsUri(appId), cancellationToken);
            if (detailsJson is null)
            {
                _logger.LogWarning("Steam store API returned HTTP {Status} for app {AppId}", (int)status, appId);
                return Result<MediaPayload, ExtractionError>.Failure(
                    new ExtractionError.NetworkFailure(url, $"HTTP {(int)status}"));
            }

            if (SteamAppDetailsParser.Parse(detailsJson, appId) is not { } app)
            {
                // Delisted, locked out of this storefront's region, or never existed.
                return Result<MediaPayload, ExtractionError>.Failure(
                    new ExtractionError.ContentUnavailable(url, $"the store has no page for app {appId}"));
            }

            var reviews = await TryGetReviewsAsync(client, appId, cancellationToken);
            var photos = await DownloadPhotosAsync(client, PickPhotoUrls(app), cancellationToken);

            var albumBudget = (long)_downloadOptions.MaxFileSizeMb * 1024 * 1024 - RequestFramingBytes;
            var trailerBudget = albumBudget - photos.Sum(p => p.SizeBytes ?? 0);
            var trailer = app.TrailerManifestUrl is { } manifestUrl
                ? await TryGetTrailerAsync(client, appId, manifestUrl, trailerBudget, cancellationToken)
                : null;

            List<MediaItem> items = trailer is null ? photos : [trailer, .. photos];

            return Result<MediaPayload, ExtractionError>.Success(new MediaPayload(
                url,
                Title: app.Name,
                Author: app.Developer,
                Items: items,
                Description: SteamCaptionBuilder.Build(app, reviews)));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Steam HTTP failure for app {AppId}", appId);
            return Result<MediaPayload, ExtractionError>.Failure(new ExtractionError.NetworkFailure(url, ex.Message));
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Steam HTTP call timed out for app {AppId}", appId);
            return Result<MediaPayload, ExtractionError>.Failure(new ExtractionError.NetworkFailure(url, "timed out"));
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Steam store API answered app {AppId} with something other than JSON", appId);
            return Result<MediaPayload, ExtractionError>.Failure(new ExtractionError.ToolFailure(ex.Message));
        }
    }

    private Uri AppDetailsUri(int appId) => new(
        "https://store.steampowered.com/api/appdetails" +
        $"?appids={appId.ToString(CultureInfo.InvariantCulture)}" +
        $"&cc={Uri.EscapeDataString(_options.CountryCode)}" +
        $"&l={Uri.EscapeDataString(_options.Language)}");

    private Uri ReviewsUri(int appId) => new(
        $"https://store.steampowered.com/appreviews/{appId.ToString(CultureInfo.InvariantCulture)}" +
        "?json=1&language=all&purchase_type=all&num_per_page=0" +
        $"&l={Uri.EscapeDataString(_options.Language)}");

    /// <summary>
    /// Screenshots fill the album slots the trailer leaves. The header capsule stands in only when the
    /// store lists no screenshots at all — it is a logo banner, not a look at the game.
    /// </summary>
    private static List<Uri> PickPhotoUrls(SteamApp app)
    {
        var slots = app.TrailerManifestUrl is null ? MaxAlbumItems : MaxAlbumItems - 1;

        if (app.ScreenshotUrls.Count > 0)
        {
            return app.ScreenshotUrls.Take(slots).ToList();
        }

        return app.HeaderImageUrl is { } header ? [header] : [];
    }

    /// <summary>The review verdict, or null — the caption simply goes without that line.</summary>
    private async Task<SteamReviewSummary?> TryGetReviewsAsync(
        HttpClient client,
        int appId,
        CancellationToken cancellationToken)
    {
        try
        {
            var (status, json) = await GetStringAsync(client, ReviewsUri(appId), cancellationToken);
            if (json is null)
            {
                _logger.LogWarning("Steam reviews API returned HTTP {Status} for app {AppId}", (int)status, appId);
                return null;
            }

            return SteamReviewSummary.Parse(json);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
            || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogWarning(ex, "Steam review summary unavailable for app {AppId}", appId);
            return null;
        }
    }

    /// <summary>
    /// The trailer as an mp4, at the tallest rendition the budget allows — or null, in which case the
    /// album simply goes without it. A trailer too long for any rendition is a normal outcome; a
    /// manifest or download that fails is not, and is logged as a warning.
    /// </summary>
    private async Task<MediaItem?> TryGetTrailerAsync(
        HttpClient client,
        int appId,
        Uri manifestUrl,
        long budgetBytes,
        CancellationToken cancellationToken)
    {
        SteamTrailerManifest? manifest;
        try
        {
            var (status, mpd) = await GetStringAsync(client, manifestUrl, cancellationToken);
            if (mpd is null)
            {
                _logger.LogWarning(
                    "Steam trailer manifest returned HTTP {Status} for app {AppId}", (int)status, appId);
                return null;
            }

            manifest = SteamTrailerManifest.Parse(mpd);
        }
        catch (Exception ex) when (ex is HttpRequestException or XmlException
            || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogWarning(ex, "Steam trailer manifest unreadable for app {AppId}", appId);
            return null;
        }

        if (manifest is null)
        {
            _logger.LogWarning(
                "Steam trailer manifest for app {AppId} names no duration or no H.264 rendition", appId);
            return null;
        }

        if (manifest.PickRendition(budgetBytes, MaxTrailerHeight) is not { } rendition)
        {
            _logger.LogInformation(
                "Skipping the Steam trailer for app {AppId}: {DurationSeconds}s runs past {BudgetMb}MB at every rendition",
                appId, (int)manifest.Duration.TotalSeconds, budgetBytes / (1024 * 1024));
            return null;
        }

        var download = await _trailerDownloader.DownloadAsync(manifestUrl, rendition.Height, cancellationToken);
        if (download is not Result<string, ExtractionError>.Ok { Value: var filePath })
        {
            _logger.LogWarning(
                "Steam trailer download failed for app {AppId}: {Reason}",
                appId, download.Match(_ => string.Empty, error => error.Reason));
            return null;
        }

        var size = new FileInfo(filePath).Length;
        if (size > budgetBytes)
        {
            _logger.LogInformation(
                "Discarding the Steam trailer for app {AppId}: {SizeMb}MB at {Height}p came out over its {BudgetMb}MB budget",
                appId, size / (1024 * 1024), rendition.Height, budgetBytes / (1024 * 1024));
            BestEffortDelete(filePath);
            return null;
        }

        return new MediaItem(
            FilePath: filePath,
            Kind: MediaKind.Video,
            MimeType: "video/mp4",
            SizeBytes: size,
            DurationSeconds: (int)Math.Round(manifest.Duration.TotalSeconds));
    }

    private async Task<List<MediaItem>> DownloadPhotosAsync(
        HttpClient client,
        List<Uri> photoUrls,
        CancellationToken cancellationToken)
    {
        var items = new List<MediaItem>(photoUrls.Count);

        foreach (var photoUrl in photoUrls)
        {
            if (await DownloadPhotoAsync(client, photoUrl, cancellationToken) is { } item)
            {
                items.Add(item);
            }
        }

        return items;
    }

    private async Task<MediaItem?> DownloadPhotoAsync(
        HttpClient client,
        Uri photoUrl,
        CancellationToken cancellationToken)
    {
        var filePath = Path.Combine(_downloadOptions.ResolvedDownloadDirectory, $"steam_{Guid.NewGuid():N}.jpg");

        try
        {
            using var response = await SendAsync(
                client, photoUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Steam screenshot fetch returned HTTP {Status}", (int)response.StatusCode);
                return null;
            }

            if (response.Content.Headers.ContentLength is > MaxPhotoBytes)
            {
                _logger.LogInformation("Skipping a Steam screenshot over Telegram's photo limit");
                return null;
            }

            bool withinCap;
            await using (var fileStream = File.Create(filePath))
            await using (var httpStream = await response.Content.ReadAsStreamAsync(cancellationToken))
            {
                withinCap = await CopyWithinCapAsync(httpStream, fileStream, MaxPhotoBytes, cancellationToken);
            }

            if (!withinCap)
            {
                _logger.LogInformation("Discarding a Steam screenshot whose stream ran over Telegram's photo limit");
                BestEffortDelete(filePath);
                return null;
            }

            return new MediaItem(
                FilePath: filePath,
                Kind: MediaKind.Photo,
                MimeType: response.Content.Headers.ContentType?.MediaType ?? "image/jpeg",
                SizeBytes: new FileInfo(filePath).Length,
                DurationSeconds: null);
        }
        catch (Exception ex) when (ex is HttpRequestException
            || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogWarning(ex, "Steam screenshot download failed for one item");
            BestEffortDelete(filePath);
            return null;
        }
    }

    private async Task<(HttpStatusCode Status, string? Body)> GetStringAsync(
        HttpClient client,
        Uri uri,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(client, uri, HttpCompletionOption.ResponseContentRead, cancellationToken);

        return response.IsSuccessStatusCode
            ? (response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken))
            : (response.StatusCode, null);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        Uri uri,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken) =>
        await _retryPipeline.ExecuteAsync(async token =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd(BrowserUserAgent);
            return await client.SendAsync(request, completionOption, token);
        }, cancellationToken);

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

    private void BestEffortDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Could not delete {Path}", path);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogDebug(ex, "Could not delete {Path}", path);
        }
    }
}
