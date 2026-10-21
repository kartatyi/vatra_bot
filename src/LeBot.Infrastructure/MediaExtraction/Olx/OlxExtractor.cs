using System.Net;
using System.Text.RegularExpressions;
using LeBot.Application.Ports;
using LeBot.Domain.Common;
using LeBot.Domain.Media;
using LeBot.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

namespace LeBot.Infrastructure.MediaExtraction.Olx;

/// <summary>
/// Extracts an olx.ua advert as a photo album under a caption with the price, place, the category's
/// parameters and the seller's description. OLX isn't in yt-dlp's site list, so this is a dedicated
/// extractor: it fetches the advert page, which renders in full for an anonymous browser — no login,
/// no cookies — and reads it via <see cref="OlxListingParser"/>.
/// </summary>
/// <remarks>
/// Job adverts (<c>/obyavlenie/rabota/…</c>) are left alone: their page carries a different state shape
/// and, as a rule, no photos.
/// </remarks>
public sealed partial class OlxExtractor : IPlatformExtractor
{
    private static readonly string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/120.0.0.0 Safari/537.36";

    // /d/uk/obyavlenie/<slug>-ID11svhc.html and the language-less /d/obyavlenie/… alike; the code after
    // "-ID" identifies the advert. A job sits one segment deeper (/obyavlenie/rabota/<slug>-ID….html),
    // so the single-segment slug is also what leaves jobs out.
    [GeneratedRegex(@"^/(?:d/)?(?:[a-z]{2}/)?obyavlenie/[^/]+-ID[0-9A-Za-z]+\.html$", RegexOptions.IgnoreCase)]
    private static partial Regex AdvertPath();

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly YtDlpOptions _options;
    private readonly ILogger<OlxExtractor> _logger;
    private readonly ResiliencePipeline<HttpResponseMessage> _retryPipeline;

    public OlxExtractor(
        IHttpClientFactory httpClientFactory,
        IOptions<YtDlpOptions> options,
        ILogger<OlxExtractor> logger)
    {
        _httpClientFactory = httpClientFactory;
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
                        "Retrying OLX HTTP call (attempt {Attempt} after {DelayMs}ms): {Reason}",
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
        else if (host.StartsWith("m.", StringComparison.OrdinalIgnoreCase))
        {
            host = host[2..];
        }

        return host.Equals("olx.ua", StringComparison.OrdinalIgnoreCase)
            && AdvertPath().IsMatch(url.AbsolutePath);
    }

    public async Task<Result<MediaPayload, ExtractionError>> ExtractAsync(
        Uri url,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(nameof(OlxExtractor));

        try
        {
            using var response = await _retryPipeline.ExecuteAsync(async token =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd(BrowserUserAgent);
                return await client.SendAsync(request, token);
            }, cancellationToken);

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                return Result<MediaPayload, ExtractionError>.Failure(
                    new ExtractionError.ContentUnavailable(url, "the advert was removed"));
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("OLX page returned HTTP {Status} for {Url}", (int)response.StatusCode, url);
                return Result<MediaPayload, ExtractionError>.Failure(
                    new ExtractionError.NetworkFailure(url, $"HTTP {(int)response.StatusCode}"));
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);

            if (OlxListingParser.Parse(html) is not { } listing)
            {
                // The URL looked like an advert, but the page didn't carry one: OLX redirected to search,
                // or changed its page state. Nothing to post — and worth a look if it keeps happening.
                _logger.LogWarning("OLX page for {Url} carried no advert state", url);
                return Result<MediaPayload, ExtractionError>.Success(
                    new MediaPayload(url, Title: null, Author: null, Items: []));
            }

            var caption = OlxCaptionBuilder.Build(listing);
            var items = await DownloadPhotosAsync(client, listing.PhotoUrls, cancellationToken);

            // Even with no photos the caption stays: the handler falls back to a text reply, so the
            // advert's facts still land in the chat.
            return Result<MediaPayload, ExtractionError>.Success(
                new MediaPayload(url, Title: null, Author: null, Items: items, Description: caption));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "OLX HTTP failure for {Url}", url);
            return Result<MediaPayload, ExtractionError>.Failure(new ExtractionError.NetworkFailure(url, ex.Message));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Unexpected error extracting OLX {Url}", url);
            return Result<MediaPayload, ExtractionError>.Failure(new ExtractionError.ToolFailure(ex.Message));
        }
    }

    /// <summary>All photos at once, kept in gallery order; one that fails is dropped, not fatal.</summary>
    private async Task<List<MediaItem>> DownloadPhotosAsync(
        HttpClient client,
        IReadOnlyList<string> photoUrls,
        CancellationToken cancellationToken)
    {
        var maxBytes = (long)_options.MaxFileSizeMb * 1024 * 1024;
        var downloads = await Task.WhenAll(
            photoUrls.Select(photoUrl => DownloadPhotoAsync(client, photoUrl, maxBytes, cancellationToken)));

        return [.. downloads.OfType<MediaItem>()];
    }

    private async Task<MediaItem?> DownloadPhotoAsync(
        HttpClient client,
        string photoUrl,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _retryPipeline.ExecuteAsync(async token =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, photoUrl);
                request.Headers.UserAgent.ParseAdd(BrowserUserAgent);
                // OLX's CDN negotiates the format: a browser's Accept gets WebP, which Telegram won't take as a photo.
                request.Headers.Accept.ParseAdd("image/jpeg");
                return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            }, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("OLX photo fetch returned HTTP {Status}", (int)response.StatusCode);
                return null;
            }

            if (response.Content.Headers.ContentLength is long length && length > maxBytes)
            {
                _logger.LogInformation(
                    "Skipping OLX photo: size {SizeMb}MB exceeds limit {LimitMb}MB",
                    length / (1024 * 1024), _options.MaxFileSizeMb);
                return null;
            }

            var filePath = Path.Combine(_options.ResolvedDownloadDirectory, $"olx_{Guid.NewGuid():N}.jpg");

            bool withinCap;
            await using (var fileStream = File.Create(filePath))
            await using (var httpStream = await response.Content.ReadAsStreamAsync(cancellationToken))
            {
                withinCap = await CopyWithinCapAsync(httpStream, fileStream, maxBytes, cancellationToken);
            }

            if (!withinCap)
            {
                _logger.LogInformation(
                    "Discarding OLX photo {Path}: stream exceeded limit {LimitMb}MB", filePath, _options.MaxFileSizeMb);
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
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "OLX photo download failed for one item");
            return null;
        }
    }

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
