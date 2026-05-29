using LeBot.Application.Ports;
using LeBot.Domain.Common;
using LeBot.Domain.Media;
using LeBot.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using YoutubeDLSharp;
using YoutubeDLSharp.Options;

namespace LeBot.Infrastructure.MediaExtraction.YtDlp;

/// <summary>
/// Universal media extractor backed by yt-dlp. Claims responsibility for a curated
/// list of hosts where the user is likely to post links; everything else falls
/// through so a future per-platform extractor can take over.
/// </summary>
public sealed class YtDlpPlatformExtractor : IPlatformExtractor
{
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
            YoutubeDLPath = ExecutablePathResolver.Resolve(_options.BinaryPath),
            OutputFolder = _options.DownloadDirectory,
        };

        if (!string.IsNullOrEmpty(_options.FfmpegPath))
        {
            _ytdl.FFmpegPath = ExecutablePathResolver.Resolve(_options.FfmpegPath);
        }
    }

    public bool CanHandle(Uri url)
    {
        // yt-dlp claims ~1800 sites — anything we'd plausibly want to repost. Rather than
        // maintaining a curated whitelist that goes stale every time TikTok ships a new short
        // domain, we claim every http(s) URL and let yt-dlp's own extractor matrix decide.
        // Unsupported hosts come back as ExtractionError.UnsupportedPlatform and the handler
        // skips them silently — no "Couldn't extract" message for random non-media URLs.
        return url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps;
    }

    public async Task<Result<MediaPayload, ExtractionError>> ExtractAsync(
        Uri url,
        CancellationToken cancellationToken)
    {
        // Defence-in-depth against CVE-2025-43858: feed the canonical, percent-encoded form
        // so any shell metacharacters from the user message are escaped before yt-dlp sees them.
        var sanitisedUrl = url.AbsoluteUri;

        var optionSet = BuildOptionSet();

        try
        {
            var metadata = await _ytdl.RunVideoDataFetch(sanitisedUrl, ct: cancellationToken, overrideOptions: optionSet);
            if (!metadata.Success || metadata.Data is null)
            {
                var detail = JoinErrors(metadata.ErrorOutput);

                if (LooksLikeUnsupportedUrl(detail))
                {
                    _logger.LogDebug("yt-dlp does not handle {Url} — leaving for other extractors / silent skip", url);
                    return Result<MediaPayload, ExtractionError>.Failure(
                        new ExtractionError.UnsupportedPlatform(url));
                }

                _logger.LogWarning("yt-dlp metadata fetch failed for {Url}: {Detail}", url, detail);
                return Result<MediaPayload, ExtractionError>.Failure(
                    new ExtractionError.ContentUnavailable(url, detail));
            }

            var info = metadata.Data;

            // Some posts (Instagram image carousels seen anonymously, text-only Threads, etc.)
            // come back as playlists with zero entries — yt-dlp has nothing to download. Surface
            // this as an empty payload, not a failure: the source link's native Telegram preview
            // is already in the chat, and adding a "tool failure" log line would be noise.
            if (info.Entries is { Length: 0 })
            {
                _logger.LogInformation(
                    "Skipping {Url}: post has no playable media (likely image-only or text-only without auth)",
                    url);
                return Result<MediaPayload, ExtractionError>.Success(
                    new MediaPayload(url, info.Title, info.Uploader, [], info.Description));
            }

            // Multi-entry playlist. If every entry has no downloadable formats (the typical
            // Instagram image-carousel shape with --ignore-no-formats-error), we won't get
            // anything by trying to download. Skip straight to the text-only payload so the
            // chain can use the metadata's title/description as a reply.
            if (info.Entries is { Length: > 0 } entries)
            {
                if (AllEntriesHaveNoFormats(entries))
                {
                    _logger.LogInformation(
                        "Skipping {Url}: playlist entries carry no downloadable formats — surfacing text only",
                        url);
                    return Result<MediaPayload, ExtractionError>.Success(
                        new MediaPayload(url, info.Title, info.Uploader, [], info.Description));
                }

                if (entries.Length > 1)
                {
                    return await DownloadPlaylistAsync(url, sanitisedUrl, info, optionSet, cancellationToken);
                }
            }

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
                    new MediaPayload(url, info.Title, info.Uploader, [], info.Description));
            }

            // Some posts come back with rich metadata (title, description, uploader) but no
            // downloadable formats at all — Instagram image carousels and text-only Threads
            // posts shaped as single entries are the usual culprit. Surface the metadata as
            // text-only rather than burning a download attempt that always 404s.
            if (info.Formats is null or { Length: 0 })
            {
                _logger.LogInformation(
                    "Skipping {Url}: yt-dlp returned no downloadable formats — surfacing text only",
                    url);
                return Result<MediaPayload, ExtractionError>.Success(
                    new MediaPayload(url, info.Title, info.Uploader, [], info.Description));
            }

            // Prefer a pre-merged single-file format so we don't need ffmpeg to glue DASH
            // video and audio streams (Instagram and some other platforms only expose DASH;
            // without this selector yt-dlp grabs both streams and then "succeeds" with no
            // merged file present on disk).
            var download = await _ytdl.RunVideoDownload(
                sanitisedUrl,
                format: "best[ext=mp4]/best",
                ct: cancellationToken,
                overrideOptions: optionSet);
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
                    new MediaPayload(url, info.Title, info.Uploader, [], info.Description));
            }

            var item = new MediaItem(
                FilePath: filePath,
                Kind: DetermineKind(filePath),
                MimeType: GuessMimeType(filePath),
                SizeBytes: fileInfo.Length,
                DurationSeconds: info.Duration is { } d ? (int)d : null);

            return Result<MediaPayload, ExtractionError>.Success(
                new MediaPayload(url, info.Title, info.Uploader, [item], info.Description));
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

    private OptionSet BuildOptionSet()
    {
        var opts = new OptionSet();
        if (!string.IsNullOrEmpty(_options.CookiesFromBrowser))
        {
            opts.CookiesFromBrowser = _options.CookiesFromBrowser;
        }
        // Without this, Instagram image carousels (which surface as playlist entries
        // with empty formats) make yt-dlp fail metadata fetch entirely. We'd rather get
        // the title and description back and surface them as a text reply than swallow
        // them with a hard failure.
        opts.AddCustomOption("--ignore-no-formats-error", true);
        // Strip emoji and other non-ASCII characters from output filenames. yt-dlp's
        // captured-filename reporting (what YoutubeDLSharp parses into RunResult.Data)
        // does its own normalisation that disagrees with what ends up on disk when the
        // title contains emoji — TikTok in particular puts 📌 in titles and we'd then
        // look for a sanitised name that doesn't exist. --restrict-filenames keeps both
        // sides in lockstep with ASCII-only names.
        opts.AddCustomOption("--restrict-filenames", true);
        return opts;
    }

    private static bool LooksLikeUnsupportedUrl(string detail) =>
        detail.Contains("Unsupported URL", StringComparison.OrdinalIgnoreCase);

    private async Task<Result<MediaPayload, ExtractionError>> DownloadPlaylistAsync(
        Uri url,
        string sanitisedUrl,
        YoutubeDLSharp.Metadata.VideoData info,
        OptionSet optionSet,
        CancellationToken cancellationToken)
    {
        var playlistResult = await _ytdl.RunVideoPlaylistDownload(
            sanitisedUrl,
            format: "best[ext=mp4]/best",
            ct: cancellationToken,
            overrideOptions: optionSet);

        if (!playlistResult.Success || playlistResult.Data is null or { Length: 0 })
        {
            var detail = JoinErrors(playlistResult.ErrorOutput);
            _logger.LogWarning("yt-dlp playlist download failed for {Url}: {Detail}", url, detail);
            return Result<MediaPayload, ExtractionError>.Failure(
                new ExtractionError.ToolFailure(detail));
        }

        var maxBytes = (long)_options.MaxFileSizeMb * 1024 * 1024;
        var items = new List<MediaItem>(playlistResult.Data.Length);

        foreach (var filePath in playlistResult.Data)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                _logger.LogWarning("Playlist entry path is missing or empty: {Path}", filePath);
                continue;
            }

            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length > maxBytes)
            {
                _logger.LogInformation(
                    "Dropping {Path} from album: size {SizeMb}MB exceeds limit {LimitMb}MB",
                    filePath, fileInfo.Length / (1024 * 1024), _options.MaxFileSizeMb);
                BestEffortDelete(filePath);
                continue;
            }

            items.Add(new MediaItem(
                FilePath: filePath,
                Kind: DetermineKind(filePath),
                MimeType: GuessMimeType(filePath),
                SizeBytes: fileInfo.Length,
                DurationSeconds: null));
        }

        return Result<MediaPayload, ExtractionError>.Success(
            new MediaPayload(url, info.Title, info.Uploader, items, info.Description));
    }

    private static bool AllEntriesHaveNoFormats(YoutubeDLSharp.Metadata.VideoData[] entries)
    {
        foreach (var entry in entries)
        {
            if (entry.Formats is { Length: > 0 })
            {
                return false;
            }
        }
        return true;
    }

    private static MediaKind DetermineKind(string filePath) => Path.GetExtension(filePath).ToLowerInvariant() switch
    {
        ".mp4" or ".webm" or ".mov" or ".mkv" => MediaKind.Video,
        ".jpg" or ".jpeg" or ".png" or ".webp" => MediaKind.Photo,
        ".gif" => MediaKind.Animation,
        ".mp3" or ".m4a" or ".ogg" or ".wav" => MediaKind.Audio,
        _ => MediaKind.Video,
    };

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
