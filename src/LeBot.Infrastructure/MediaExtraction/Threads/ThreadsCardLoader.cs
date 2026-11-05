using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Web;
using LeBot.Domain.Media;
using LeBot.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

namespace LeBot.Infrastructure.MediaExtraction.Threads;

/// <summary>
/// Reads the <c>og:image</c> Threads exposes to crawler User-Agents (Twitterbot, facebookexternalhit,
/// Googlebot) even though anonymous browsers see only a logged-out shell — mimicking a crawler is
/// the whole trick. yt-dlp can't stand in: its Threads extractor matches only <c>threads.net</c>,
/// and Meta moved the canonical domain to <c>threads.com</c>.
/// </summary>
internal sealed partial class ThreadsCardLoader : IThreadsCardLoader
{
    private const string CrawlerUserAgent = "Twitterbot/1.0";

    [GeneratedRegex(@"<meta\s+property=""og:image""\s+content=""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex OgImagePattern();

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly YtDlpOptions _options;
    private readonly ILogger<ThreadsCardLoader> _logger;
    private readonly ResiliencePipeline<HttpResponseMessage> _retryPipeline;

    public ThreadsCardLoader(
        IHttpClientFactory httpClientFactory,
        IOptions<YtDlpOptions> options,
        ILogger<ThreadsCardLoader> logger)
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
                        "Retrying Threads card call (attempt {Attempt} after {DelayMs}ms): {Reason}",
                        args.AttemptNumber + 1,
                        args.RetryDelay.TotalMilliseconds,
                        args.Outcome.Exception?.Message ?? $"status {args.Outcome.Result?.StatusCode}");
                    return ValueTask.CompletedTask;
                },
            })
            .Build();
    }

    public async Task<MediaItem?> LoadAsync(Uri url, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(nameof(ThreadsCardLoader));

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
                _logger.LogWarning(
                    "Threads card page returned HTTP {Status} for {Url}", (int)response.StatusCode, url);
                return null;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            var match = OgImagePattern().Match(html);
            if (!match.Success)
            {
                _logger.LogInformation("Threads page for {Url} carries no og:image card", url);
                return null;
            }

            return await DownloadAsync(client, HttpUtility.HtmlDecode(match.Groups[1].Value), cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Threads card fetch failed for {Url}", url);
            return null;
        }
    }

    private async Task<MediaItem?> DownloadAsync(
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
                _logger.LogWarning(
                    "Threads card image returned HTTP {Status} for {Url}", (int)response.StatusCode, imageUrl);
                return null;
            }

            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength is > 0 && contentLength > maxBytes)
            {
                _logger.LogInformation(
                    "Skipping Threads card {Url}: predicted size {SizeMb}MB exceeds limit {LimitMb}MB",
                    imageUrl, contentLength / (1024 * 1024), _options.MaxFileSizeMb);
                return null;
            }

            var extension = GuessExtension(response.Content.Headers.ContentType) ?? ".jpg";
            var filePath = Path.Combine(_options.ResolvedDownloadDirectory, $"threads_{Guid.NewGuid():N}{extension}");

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
            _logger.LogWarning(ex, "Threads card download failed for {Url}", imageUrl);
            return null;
        }
    }

    private static bool IsTransientStatus(HttpStatusCode status) =>
        status == HttpStatusCode.RequestTimeout
        || status == HttpStatusCode.TooManyRequests
        || ((int)status >= 500 && (int)status < 600);

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
