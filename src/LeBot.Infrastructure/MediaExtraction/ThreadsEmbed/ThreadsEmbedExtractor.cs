using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Web;
using LeBot.Application.Ports;
using LeBot.Domain.Common;
using LeBot.Domain.Media;
using LeBot.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

namespace LeBot.Infrastructure.MediaExtraction.ThreadsEmbed;

/// <summary>
/// First-pass extractor for Threads posts. yt-dlp's Threads extractor only matches
/// <c>threads.net</c>, but Meta moved the canonical domain to <c>threads.com</c> and the
/// .net URLs redirect to .com — leaving yt-dlp unable to handle either. Threads exposes
/// Open Graph metadata to crawler User-Agents (Twitterbot, facebookexternalhit, Googlebot)
/// even though anonymous browsers see only a logged-out shell. We mimic Twitterbot to pull
/// out the author, the preview image (which Threads renders the post body into for text-only
/// posts), and ship them as a single-photo payload.
/// </summary>
public sealed partial class ThreadsEmbedExtractor : IPlatformExtractor
{
    private static readonly string CrawlerUserAgent = "Twitterbot/1.0";

    [GeneratedRegex(@"<meta\s+property=""og:title""\s+content=""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex OgTitlePattern();

    [GeneratedRegex(@"<meta\s+property=""og:image""\s+content=""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex OgImagePattern();

    [GeneratedRegex(@"^.*?\(@([\w.]+)\)\s+on\s+Threads", RegexOptions.IgnoreCase)]
    private static partial Regex AuthorFromOgTitle();

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly YtDlpOptions _options;
    private readonly ILogger<ThreadsEmbedExtractor> _logger;
    private readonly ResiliencePipeline<HttpResponseMessage> _retryPipeline;

    public ThreadsEmbedExtractor(
        IHttpClientFactory httpClientFactory,
        IOptions<YtDlpOptions> options,
        ILogger<ThreadsEmbedExtractor> logger)
    {
        _httpClientFactory = httpClientFactory;
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
                        "Retrying Threads HTTP call (attempt {Attempt} after {DelayMs}ms): {Reason}",
                        args.AttemptNumber + 1,
                        args.RetryDelay.TotalMilliseconds,
                        args.Outcome.Exception?.Message ?? $"status {args.Outcome.Result?.StatusCode}");
                    return ValueTask.CompletedTask;
                },
            })
            .Build();
    }

    private static bool IsTransientStatus(HttpStatusCode status) =>
        status == HttpStatusCode.RequestTimeout
        || status == HttpStatusCode.TooManyRequests
        || ((int)status >= 500 && (int)status < 600);

    public bool CanHandle(Uri url)
    {
        var host = url.Host;
        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            host = host[4..];
        }

        if (!host.Equals("threads.com", StringComparison.OrdinalIgnoreCase)
            && !host.Equals("threads.net", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return url.AbsolutePath.Contains("/post/", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<Result<MediaPayload, ExtractionError>> ExtractAsync(
        Uri url,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(nameof(ThreadsEmbedExtractor));

        try
        {
            using var response = await _retryPipeline.ExecuteAsync(async token =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.Clear();
                request.Headers.UserAgent.TryParseAdd(CrawlerUserAgent);
                return await client.SendAsync(request, token);
            }, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Threads page returned HTTP {Status} for {Url}", (int)response.StatusCode, url);
                return Result<MediaPayload, ExtractionError>.Failure(
                    new ExtractionError.NetworkFailure(url, $"HTTP {(int)response.StatusCode}"));
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);

            var rawTitle = ExtractFirst(html, OgTitlePattern());
            var rawImage = ExtractFirst(html, OgImagePattern());

            var decodedTitle = DecodeHtml(rawTitle);
            var imageUrl = DecodeHtml(rawImage);
            var author = ExtractAuthor(decodedTitle);

            if (string.IsNullOrEmpty(imageUrl))
            {
                _logger.LogInformation("Threads page for {Url} carried no og:image — falling through to text-only", url);
                return Result<MediaPayload, ExtractionError>.Success(
                    new MediaPayload(url, Title: null, Author: author, Items: [], Description: decodedTitle));
            }

            var item = await DownloadImageAsync(client, imageUrl, cancellationToken);
            if (item is null)
            {
                _logger.LogWarning("Threads og:image download failed for {Url}", url);
                return Result<MediaPayload, ExtractionError>.Success(
                    new MediaPayload(url, Title: null, Author: author, Items: [], Description: decodedTitle));
            }

            return Result<MediaPayload, ExtractionError>.Success(
                new MediaPayload(url, Title: null, Author: author, Items: [item], Description: decodedTitle));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Threads HTTP failure for {Url}", url);
            return Result<MediaPayload, ExtractionError>.Failure(
                new ExtractionError.NetworkFailure(url, ex.Message));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Unexpected error scraping Threads for {Url}", url);
            return Result<MediaPayload, ExtractionError>.Failure(
                new ExtractionError.ToolFailure(ex.Message));
        }
    }

    private static string? ExtractFirst(string html, Regex pattern)
    {
        var match = pattern.Match(html);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? DecodeHtml(string? raw)
        => string.IsNullOrEmpty(raw) ? raw : HttpUtility.HtmlDecode(raw);

    private static string? ExtractAuthor(string? decodedTitle)
    {
        if (string.IsNullOrEmpty(decodedTitle))
        {
            return null;
        }

        var match = AuthorFromOgTitle().Match(decodedTitle);
        return match.Success ? match.Groups[1].Value : null;
    }

    private async Task<MediaItem?> DownloadImageAsync(
        HttpClient client,
        string imageUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            var maxBytes = (long)_options.MaxFileSizeMb * 1024 * 1024;

            using var response = await _retryPipeline.ExecuteAsync(async token =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, imageUrl);
                request.Headers.UserAgent.Clear();
                request.Headers.UserAgent.TryParseAdd(CrawlerUserAgent);
                return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            }, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("og:image fetch returned HTTP {Status} for {Url}", (int)response.StatusCode, imageUrl);
                return null;
            }

            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength is > 0 && contentLength > maxBytes)
            {
                _logger.LogInformation(
                    "Skipping Threads image {Url}: predicted size {SizeMb}MB exceeds limit {LimitMb}MB",
                    imageUrl, contentLength / (1024 * 1024), _options.MaxFileSizeMb);
                return null;
            }

            var extension = GuessExtension(response.Content.Headers.ContentType) ?? ".jpg";
            var filePath = Path.Combine(_options.DownloadDirectory, $"threads_{Guid.NewGuid():N}{extension}");

            await using (var fileStream = File.Create(filePath))
            await using (var httpStream = await response.Content.ReadAsStreamAsync(cancellationToken))
            {
                await httpStream.CopyToAsync(fileStream, cancellationToken);
            }

            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length > maxBytes)
            {
                _logger.LogInformation(
                    "Discarding {Path}: actual size {SizeMb}MB exceeds limit {LimitMb}MB",
                    filePath, fileInfo.Length / (1024 * 1024), _options.MaxFileSizeMb);
                TryDelete(filePath);
                return null;
            }

            return new MediaItem(
                FilePath: filePath,
                Kind: MediaKind.Photo,
                MimeType: response.Content.Headers.ContentType?.MediaType ?? "image/jpeg",
                SizeBytes: fileInfo.Length,
                DurationSeconds: null);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Threads image download failed for {Url}", imageUrl);
            return null;
        }
    }

    private static string? GuessExtension(MediaTypeHeaderValue? contentType) => contentType?.MediaType?.ToLowerInvariant() switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        _ => null,
    };

    private static void TryDelete(string filePath)
    {
        try
        {
            File.Delete(filePath);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
