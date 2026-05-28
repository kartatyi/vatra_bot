using LeBot.Application.Ports;
using LeBot.Domain.Common;
using LeBot.Domain.Media;
using LeBot.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using YoutubeDLSharp;

namespace LeBot.Infrastructure.MediaExtraction.YtDlp;

/// <summary>
/// Universal media extractor backed by yt-dlp. Claims responsibility for a curated
/// list of hosts where the user is likely to post links; everything else falls
/// through so a future per-platform extractor can take over.
/// </summary>
public sealed class YtDlpPlatformExtractor : IPlatformExtractor
{
    private static readonly HashSet<string> SupportedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "tiktok.com",
        "vm.tiktok.com",
        "youtube.com",
        "youtu.be",
        "instagram.com",
        "threads.net",
        "threads.com",
        "twitter.com",
        "x.com",
        "reddit.com",
        "redd.it",
        "facebook.com",
        "fb.watch",
        "vimeo.com",
        "twitch.tv",
        "clips.twitch.tv",
    };

    private readonly YoutubeDL _ytdl;
    private readonly YtDlpOptions _options;
    private readonly ILogger<YtDlpPlatformExtractor> _logger;

    public YtDlpPlatformExtractor(
        IOptions<YtDlpOptions> options,
        ILogger<YtDlpPlatformExtractor> logger)
    {
        _options = options.Value;
        _logger = logger;

        Directory.CreateDirectory(_options.DownloadDirectory);

        _ytdl = new YoutubeDL
        {
            YoutubeDLPath = _options.BinaryPath,
            OutputFolder = _options.DownloadDirectory,
        };

        if (!string.IsNullOrEmpty(_options.FfmpegPath))
        {
            _ytdl.FFmpegPath = _options.FfmpegPath;
        }
    }

    public bool CanHandle(Uri url)
    {
        var host = url.Host;
        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            host = host[4..];
        }

        return SupportedHosts.Contains(host);
    }

    public async Task<Result<MediaPayload, ExtractionError>> ExtractAsync(
        Uri url,
        CancellationToken cancellationToken)
    {
        // Defence-in-depth against CVE-2025-43858: feed the canonical, percent-encoded form
        // so any shell metacharacters from the user message are escaped before yt-dlp sees them.
        var sanitisedUrl = url.AbsoluteUri;

        try
        {
            var metadata = await _ytdl.RunVideoDataFetch(sanitisedUrl, ct: cancellationToken);
            if (!metadata.Success || metadata.Data is null)
            {
                var detail = JoinErrors(metadata.ErrorOutput);
                _logger.LogWarning("yt-dlp metadata fetch failed for {Url}: {Detail}", url, detail);
                return Result<MediaPayload, ExtractionError>.Failure(
                    new ExtractionError.ContentUnavailable(url, detail));
            }

            var info = metadata.Data;
            var maxBytes = (long)_options.MaxFileSizeMb * 1024 * 1024;

            var predictedSize = info.Formats?
                .Select(f => f.FileSize ?? f.ApproximateFileSize)
                .OfType<long>()
                .DefaultIfEmpty(0L)
                .Max() ?? 0L;

            if (predictedSize > maxBytes)
            {
                _logger.LogInformation(
                    "Skipping download of {Url}: predicted size {SizeMb}MB exceeds limit {LimitMb}MB",
                    url, predictedSize / (1024 * 1024), _options.MaxFileSizeMb);
                return Result<MediaPayload, ExtractionError>.Success(
                    new MediaPayload(url, info.Title, info.Uploader, []));
            }

            var download = await _ytdl.RunVideoDownload(sanitisedUrl, ct: cancellationToken);
            if (!download.Success || string.IsNullOrEmpty(download.Data))
            {
                var detail = JoinErrors(download.ErrorOutput);
                _logger.LogWarning("yt-dlp download failed for {Url}: {Detail}", url, detail);
                return Result<MediaPayload, ExtractionError>.Failure(
                    new ExtractionError.ToolFailure(detail));
            }

            var filePath = download.Data;
            if (!File.Exists(filePath))
            {
                _logger.LogWarning("yt-dlp reported success but output file {Path} is missing", filePath);
                return Result<MediaPayload, ExtractionError>.Failure(
                    new ExtractionError.ToolFailure($"output file missing: {filePath}"));
            }

            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length > maxBytes)
            {
                _logger.LogInformation(
                    "Discarding {Path}: actual size {SizeMb}MB exceeds limit {LimitMb}MB",
                    filePath, fileInfo.Length / (1024 * 1024), _options.MaxFileSizeMb);
                BestEffortDelete(filePath);
                return Result<MediaPayload, ExtractionError>.Success(
                    new MediaPayload(url, info.Title, info.Uploader, []));
            }

            var item = new MediaItem(
                FilePath: filePath,
                Kind: MediaKind.Video,
                MimeType: GuessMimeType(filePath),
                SizeBytes: fileInfo.Length,
                DurationSeconds: info.Duration is { } d ? (int)d : null);

            return Result<MediaPayload, ExtractionError>.Success(
                new MediaPayload(url, info.Title, info.Uploader, [item]));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Unexpected error extracting {Url}", url);
            return Result<MediaPayload, ExtractionError>.Failure(
                new ExtractionError.ToolFailure(ex.Message));
        }
    }

    private static string JoinErrors(IEnumerable<string> errorOutput)
    {
        var joined = string.Join("; ", errorOutput);
        return string.IsNullOrWhiteSpace(joined) ? "no error output" : joined;
    }

    private static void BestEffortDelete(string filePath)
    {
        try
        {
            File.Delete(filePath);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string GuessMimeType(string filePath) => Path.GetExtension(filePath).ToLowerInvariant() switch
    {
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        ".mov" => "video/quicktime",
        ".mkv" => "video/x-matroska",
        ".mp3" => "audio/mpeg",
        ".m4a" => "audio/mp4",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => "application/octet-stream",
    };
}
