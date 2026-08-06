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

namespace LeBot.Infrastructure.MediaExtraction.AutoRia;

/// <summary>
/// Extracts an auto.ria advert as a photo album with a formatted spec caption. auto.ria isn't in
/// yt-dlp's site list and carries no video, so this is a dedicated extractor: it fetches the
/// server-rendered advert page, parses it via <see cref="AutoRiaListingParser"/>, and reposts up to
/// ten gallery photos with the make/model, generation, mileage, gearbox, engine, location, price and
/// description folded into the caption. No login or cookies are needed — the page renders in full for
/// an anonymous browser User-Agent.
/// </summary>
public sealed partial class AutoRiaExtractor : IPlatformExtractor
{
    private static readonly string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/120.0.0.0 Safari/537.36";

    [GeneratedRegex(@"(?:^|/)(?:new)?auto_[^/]*_\d+\.html$", RegexOptions.IgnoreCase)]
    private static partial Regex AdvertPath();

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly YtDlpOptions _options;
    private readonly ILogger<AutoRiaExtractor> _logger;
    private readonly ResiliencePipeline<HttpResponseMessage> _retryPipeline;

    public AutoRiaExtractor(
        IHttpClientFactory httpClientFactory,
        IOptions<YtDlpOptions> options,
        ILogger<AutoRiaExtractor> logger)
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
                        "Retrying auto.ria HTTP call (attempt {Attempt} after {DelayMs}ms): {Reason}",
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

        if (host.StartsWith("m.", StringComparison.OrdinalIgnoreCase))
        {
            host = host[2..];
        }

        return host.Equals("auto.ria.com", StringComparison.OrdinalIgnoreCase)
            && AdvertPath().IsMatch(url.AbsolutePath);
    }

    public async Task<Result<MediaPayload, ExtractionError>> ExtractAsync(
        Uri url,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(nameof(AutoRiaExtractor));

        try
        {
            using var response = await _retryPipeline.ExecuteAsync(async token =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd(BrowserUserAgent);
                return await client.SendAsync(request, token);
            }, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("auto.ria page returned HTTP {Status} for {Url}", (int)response.StatusCode, url);
                return Result<MediaPayload, ExtractionError>.Failure(
                    new ExtractionError.NetworkFailure(url, $"HTTP {(int)response.StatusCode}"));
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);

            var listing = AutoRiaListingParser.Parse(html, url);
            if (listing is null)
            {
                // The URL looked like an advert but the page didn't render as one (deleted, redirected
                // to search, or captcha). Fall through silently rather than posting an error.
                _logger.LogInformation("auto.ria page for {Url} carried no advert data", url);
                return Result<MediaPayload, ExtractionError>.Success(
                    new MediaPayload(url, Title: null, Author: null, Items: []));
            }

            var caption = AutoRiaCaptionBuilder.Build(listing);
            var items = await DownloadPhotosAsync(client, listing.PhotoUrls, cancellationToken);

            // Even with zero photos we keep the caption: the handler falls back to a text reply, so the
            // spec sheet still lands in the chat.
            return Result<MediaPayload, ExtractionError>.Success(
                new MediaPayload(url, Title: null, Author: null, Items: items, Description: caption));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "auto.ria HTTP failure for {Url}", url);
            return Result<MediaPayload, ExtractionError>.Failure(new ExtractionError.NetworkFailure(url, ex.Message));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Unexpected error extracting auto.ria {Url}", url);
            return Result<MediaPayload, ExtractionError>.Failure(new ExtractionError.ToolFailure(ex.Message));
        }
    }

    private async Task<List<MediaItem>> DownloadPhotosAsync(
        HttpClient client,
        IReadOnlyList<string> photoUrls,
        CancellationToken cancellationToken)
    {
        var maxBytes = (long)_options.MaxFileSizeMb * 1024 * 1024;
        var items = new List<MediaItem>(photoUrls.Count);

        foreach (var photoUrl in photoUrls)
        {
            var item = await DownloadPhotoAsync(client, photoUrl, maxBytes, cancellationToken);
            if (item is not null)
            {
                items.Add(item);
            }
        }

        return items;
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
                return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            }, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("auto.ria photo fetch returned HTTP {Status}", (int)response.StatusCode);
                return null;
            }

            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength is > 0 && contentLength > maxBytes)
            {
                _logger.LogInformation(
                    "Skipping auto.ria photo: predicted size {SizeMb}MB exceeds limit {LimitMb}MB",
                    contentLength / (1024 * 1024), _options.MaxFileSizeMb);
                return null;
            }

            var contentType = response.Content.Headers.ContentType?.MediaType;
            var filePath = Path.Combine(_options.ResolvedDownloadDirectory, $"ria_{Guid.NewGuid():N}.jpg");

            bool withinCap;
            await using (var fileStream = File.Create(filePath))
            await using (var httpStream = await response.Content.ReadAsStreamAsync(cancellationToken))
            {
                withinCap = await CopyWithinCapAsync(httpStream, fileStream, maxBytes, cancellationToken);
            }

            if (!withinCap)
            {
                _logger.LogInformation(
                    "Discarding auto.ria photo {Path}: stream exceeded limit {LimitMb}MB", filePath, _options.MaxFileSizeMb);
                BestEffortDelete(filePath);
                return null;
            }

            var fileInfo = new FileInfo(filePath);

            return new MediaItem(
                FilePath: filePath,
                Kind: MediaKind.Photo,
                MimeType: contentType ?? "image/jpeg",
                SizeBytes: fileInfo.Length,
                DurationSeconds: null);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "auto.ria photo download failed for one item");
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
