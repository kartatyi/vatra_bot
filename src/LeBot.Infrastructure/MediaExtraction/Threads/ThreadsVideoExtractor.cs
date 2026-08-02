using LeBot.Application.Ports;
using LeBot.Domain.Common;
using LeBot.Domain.Media;
using LeBot.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeBot.Infrastructure.MediaExtraction.Threads;

/// <summary>
/// Extracts the real video from a Threads post. Threads renders post media client-side, so the
/// playable URL never appears in the server HTML and yt-dlp ships no Threads extractor — we load
/// the page in a headless browser (<see cref="IBrowserVideoResolver"/>) and download the rendered
/// &lt;video&gt; source. Registered <em>before</em> <c>ThreadsEmbedExtractor</c>: a video post
/// yields the clip; a photo / text-only post carries no &lt;video&gt;, so this declines and the
/// embed extractor's og:image thumbnail takes over — never worse than before. See ADR 0006.
/// </summary>
internal sealed class ThreadsVideoExtractor : IPlatformExtractor
{
    private static readonly string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/120.0.0.0 Safari/537.36";

    private readonly IBrowserVideoResolver _resolver;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly YtDlpOptions _options;
    private readonly ILogger<ThreadsVideoExtractor> _logger;

    public ThreadsVideoExtractor(
        IBrowserVideoResolver resolver,
        IHttpClientFactory httpClientFactory,
        IOptions<YtDlpOptions> options,
        ILogger<ThreadsVideoExtractor> logger)
    {
        _resolver = resolver;
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;

        Directory.CreateDirectory(_options.ResolvedDownloadDirectory);
    }

    public bool CanHandle(Uri url) => ThreadsUrl.IsPost(url);

    public async Task<Result<MediaPayload, ExtractionError>> ExtractAsync(
        Uri url,
        CancellationToken cancellationToken)
    {
        string? videoUrl;
        try
        {
            videoUrl = await _resolver.ResolveVideoUrlAsync(url, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The resolver swallows its own expected failures; anything escaping is unexpected.
            // Decline so the embed extractor's thumbnail still runs.
            _logger.LogWarning(ex, "Threads video resolver threw for {Url}; declining to thumbnail fallback", url);
            return Result<MediaPayload, ExtractionError>.Failure(new ExtractionError.UnsupportedPlatform(url));
        }

        if (string.IsNullOrEmpty(videoUrl))
        {
            // No playable video on the page (photo / text-only post) or no browser available.
            // Decline quietly so ThreadsEmbedExtractor can serve the og:image.
            _logger.LogDebug("No video rendered for {Url}; leaving to thumbnail fallback", url);
            return Result<MediaPayload, ExtractionError>.Failure(new ExtractionError.UnsupportedPlatform(url));
        }

        var item = await DownloadAsync(videoUrl, cancellationToken);
        if (item is null)
        {
            // Found a clip but couldn't fetch it (oversize / network). Fall back to the thumbnail.
            _logger.LogDebug("Threads video for {Url} could not be downloaded; thumbnail fallback", url);
            return Result<MediaPayload, ExtractionError>.Failure(new ExtractionError.UnsupportedPlatform(url));
        }

        return Result<MediaPayload, ExtractionError>.Success(
            new MediaPayload(url, Title: null, Author: null, Items: [item]));
    }

    private async Task<MediaItem?> DownloadAsync(string videoUrl, CancellationToken cancellationToken)
    {
        var maxBytes = (long)_options.MaxFileSizeMb * 1024 * 1024;
        var client = _httpClientFactory.CreateClient(nameof(ThreadsVideoExtractor));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, videoUrl);
            request.Headers.UserAgent.ParseAdd(BrowserUserAgent);
            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Threads video fetch returned HTTP {Status}", (int)response.StatusCode);
                return null;
            }

            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength is > 0 && contentLength > maxBytes)
            {
                _logger.LogInformation(
                    "Skipping Threads video: predicted size {SizeMb}MB exceeds limit {LimitMb}MB",
                    contentLength / (1024 * 1024), _options.MaxFileSizeMb);
                return null;
            }

            var filePath = Path.Combine(_options.ResolvedDownloadDirectory, $"threads_{Guid.NewGuid():N}.mp4");

            // Cap while streaming, not after: a chunked CDN response has no Content-Length, so the
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
                    "Discarding Threads video {Path}: stream exceeded limit {LimitMb}MB",
                    filePath, _options.MaxFileSizeMb);
                BestEffortDelete(filePath);
                return null;
            }

            var fileInfo = new FileInfo(filePath);
            return new MediaItem(
                FilePath: filePath,
                Kind: MediaKind.Video,
                MimeType: response.Content.Headers.ContentType?.MediaType ?? "video/mp4",
                SizeBytes: fileInfo.Length,
                DurationSeconds: null);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Threads video download failed");
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
