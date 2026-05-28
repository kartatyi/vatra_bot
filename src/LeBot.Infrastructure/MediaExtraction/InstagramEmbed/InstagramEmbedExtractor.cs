using System.Text.Json;
using System.Text.RegularExpressions;
using LeBot.Application.Ports;
using LeBot.Domain.Common;
using LeBot.Domain.Media;
using LeBot.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeBot.Infrastructure.MediaExtraction.InstagramEmbed;

/// <summary>
/// Fallback extractor for Instagram <c>/p/...</c> posts (image carousels, photo posts, the cases
/// where yt-dlp's Instagram extractor returns empty entries because it focuses on video formats).
/// Pulls the embed HTML — the public endpoint Instagram exposes for external embeds — and parses
/// the image URLs and caption out of the inline JSON state.
/// </summary>
public sealed partial class InstagramEmbedExtractor : IPlatformExtractor
{
    private static readonly string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/120.0.0.0 Safari/537.36";

    [GeneratedRegex(@"display_url\\""\s*:\s*\\""([^\\""]+(?:\\.[^\\""]*)*)\\""", RegexOptions.IgnoreCase)]
    private static partial Regex DisplayUrlPattern();

    [GeneratedRegex(@"""caption""\s*:\s*""((?:[^""\\]|\\.)*)""", RegexOptions.IgnoreCase)]
    private static partial Regex CaptionPattern();

    [GeneratedRegex(@"username\\""\s*:\s*\\""([^\\""]+)\\""", RegexOptions.IgnoreCase)]
    private static partial Regex UsernamePattern();

    [GeneratedRegex(@"/t51\.\d+-\d+/(\d+_\d+_\d+)_n\.", RegexOptions.IgnoreCase)]
    private static partial Regex MediaIdPattern();

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly YtDlpOptions _options;
    private readonly ILogger<InstagramEmbedExtractor> _logger;

    public InstagramEmbedExtractor(
        IHttpClientFactory httpClientFactory,
        IOptions<YtDlpOptions> options,
        ILogger<InstagramEmbedExtractor> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;

        Directory.CreateDirectory(_options.DownloadDirectory);
    }

    public bool CanHandle(Uri url)
    {
        var host = url.Host;
        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            host = host[4..];
        }

        return host.Equals("instagram.com", StringComparison.OrdinalIgnoreCase)
            && url.AbsolutePath.StartsWith("/p/", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<Result<MediaPayload, ExtractionError>> ExtractAsync(
        Uri url,
        CancellationToken cancellationToken)
    {
        var embedUri = BuildEmbedUri(url);
        var client = _httpClientFactory.CreateClient(nameof(InstagramEmbedExtractor));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, embedUri);
            request.Headers.UserAgent.ParseAdd(BrowserUserAgent);

            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Instagram embed for {Url} returned HTTP {Status}",
                    url, (int)response.StatusCode);
                return Result<MediaPayload, ExtractionError>.Failure(
                    new ExtractionError.NetworkFailure(url, $"HTTP {(int)response.StatusCode}"));
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);

            var caption = UnescapeJson(ExtractFirst(html, CaptionPattern())) ?? UnescapeJson(ExtractFirstDoubleEscaped(html));
            var author = ExtractFirst(html, UsernamePattern());
            var imageUrls = ExtractImageUrls(html);

            if (imageUrls.Count == 0)
            {
                _logger.LogInformation("Instagram embed for {Url} contained no images", url);
                return Result<MediaPayload, ExtractionError>.Success(
                    new MediaPayload(url, Title: null, Author: author, Items: [], Description: caption));
            }

            var maxBytes = (long)_options.MaxFileSizeMb * 1024 * 1024;
            var items = new List<MediaItem>(imageUrls.Count);

            foreach (var imgUrl in imageUrls)
            {
                if (items.Count >= 10)
                {
                    break;
                }

                var item = await DownloadImageAsync(client, imgUrl, maxBytes, cancellationToken);
                if (item is not null)
                {
                    items.Add(item);
                }
            }

            if (items.Count == 0)
            {
                _logger.LogWarning("Instagram embed found {Count} image URLs but downloaded zero", imageUrls.Count);
                return Result<MediaPayload, ExtractionError>.Success(
                    new MediaPayload(url, Title: null, Author: author, Items: [], Description: caption));
            }

            return Result<MediaPayload, ExtractionError>.Success(
                new MediaPayload(url, Title: null, Author: author, Items: items, Description: caption));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Instagram embed HTTP failure for {Url}", url);
            return Result<MediaPayload, ExtractionError>.Failure(
                new ExtractionError.NetworkFailure(url, ex.Message));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Unexpected error scraping Instagram embed for {Url}", url);
            return Result<MediaPayload, ExtractionError>.Failure(
                new ExtractionError.ToolFailure(ex.Message));
        }
    }

    private static Uri BuildEmbedUri(Uri url)
    {
        var path = url.AbsolutePath.TrimEnd('/');
        return new Uri($"https://www.instagram.com{path}/embed/captioned/");
    }

    private List<string> ExtractImageUrls(string html)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        foreach (Match match in DisplayUrlPattern().Matches(html))
        {
            var rawEscaped = match.Groups[1].Value;
            var url = UnescapeJson(rawEscaped);
            if (string.IsNullOrEmpty(url))
            {
                continue;
            }

            var key = ExtractMediaId(url) ?? url;
            if (seen.Add(key))
            {
                result.Add(url);
            }
        }

        return result;
    }

    private static string? ExtractMediaId(string url)
    {
        var match = MediaIdPattern().Match(url);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? ExtractFirst(string html, Regex pattern)
    {
        var match = pattern.Match(html);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? ExtractFirstDoubleEscaped(string html)
    {
        // Caption sometimes lives in the doubly-escaped JSON state alongside display_url.
        var pattern = new Regex(@"caption\\""\s*:\s*\\""((?:[^\\""]|\\.)*)\\""", RegexOptions.IgnoreCase);
        var match = pattern.Match(html);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? UnescapeJson(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return raw;
        }

        // The embed HTML contains JSON inside JSON, so values are escaped twice. Wrap each
        // pass in quotes and ask System.Text.Json to do the heavy lifting (handles \uXXXX
        // unicode, \/, \n, etc.). Bail on the second pass if the first didn't leave escapes.
        var unescaped = TryJsonDecode(raw);
        if (unescaped is null)
        {
            return raw;
        }

        if (unescaped.Contains("\\u", StringComparison.Ordinal)
            || unescaped.Contains("\\/", StringComparison.Ordinal)
            || unescaped.Contains("\\\"", StringComparison.Ordinal))
        {
            var deeper = TryJsonDecode(unescaped);
            if (deeper is not null)
            {
                return deeper;
            }
        }

        return unescaped;
    }

    private static string? TryJsonDecode(string raw)
    {
        try
        {
            return JsonSerializer.Deserialize<string>($"\"{raw}\"");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<MediaItem?> DownloadImageAsync(
        HttpClient client,
        string imageUrl,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, imageUrl);
            request.Headers.UserAgent.ParseAdd(BrowserUserAgent);

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Image fetch returned HTTP {Status} for {Url}",
                    (int)response.StatusCode, imageUrl);
                return null;
            }

            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength is > 0 && contentLength > maxBytes)
            {
                _logger.LogInformation(
                    "Skipping image {Url}: predicted size {SizeMb}MB exceeds limit {LimitMb}MB",
                    imageUrl, contentLength / (1024 * 1024), _options.MaxFileSizeMb);
                return null;
            }

            var extension = GuessExtensionFromContentType(response.Content.Headers.ContentType?.MediaType)
                ?? GuessExtensionFromUrl(imageUrl)
                ?? ".jpg";
            var filePath = Path.Combine(_options.DownloadDirectory, $"ig_{Guid.NewGuid():N}{extension}");

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
            _logger.LogWarning(ex, "Image download failed for {Url}", imageUrl);
            return null;
        }
    }

    private static string? GuessExtensionFromContentType(string? contentType) => contentType?.ToLowerInvariant() switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        _ => null,
    };

    private static string? GuessExtensionFromUrl(string url)
    {
        var withoutQuery = url.Split('?', 2)[0];
        var ext = Path.GetExtension(withoutQuery).ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif" ? ext : null;
    }

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
