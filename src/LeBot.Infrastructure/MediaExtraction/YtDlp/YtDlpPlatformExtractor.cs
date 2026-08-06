using LeBot.Application.Ports;
using LeBot.Domain.Common;
using LeBot.Domain.Media;
using LeBot.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
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

        Directory.CreateDirectory(_options.ResolvedDownloadDirectory);

        _ytdl = new YoutubeDL
        {
            YoutubeDLPath = ExecutablePathResolver.Resolve(_options.BinaryPath),
            OutputFolder = _options.ResolvedDownloadDirectory,
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
            var metadata = await WithTransientRetryAsync(
                url,
                token => _ytdl.RunVideoDataFetch(sanitisedUrl, ct: token, overrideOptions: optionSet),
                cancellationToken);
            if (!metadata.Success || metadata.Data is null)
            {
                var detail = JoinErrors(metadata.ErrorOutput);

                // Either yt-dlp refused the URL outright, or only its catch-all [generic] extractor
                // took it and failed — the shape of a shop / news / blog link that was never media.
                // Both are "not our platform", not breakage: disown the URL so the handler skips it
                // silently and it never lands in the journal as a failure.
                if (YtDlpErrorClassifier.LooksLikeUnsupportedUrl(detail)
                    || YtDlpErrorClassifier.IsGenericExtractorFailure(detail))
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
            var download = await WithTransientRetryAsync(
                url,
                token => _ytdl.RunVideoDownload(
                    sanitisedUrl,
                    format: "best[ext=mp4]/best",
                    ct: token,
                    overrideOptions: optionSet),
                cancellationToken);
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

    internal OptionSet BuildOptionSet()
    {
        var opts = new OptionSet();
        if (!string.IsNullOrEmpty(_options.CookiesFromBrowser))
        {
            opts.CookiesFromBrowser = _options.CookiesFromBrowser;
        }
        // TikTok's bytevc1 (h265) streams advertise acodec=aac in metadata but the
        // downloaded files carry no audio track at all, and the default sort ranks
        // them "best" by resolution — so "best[ext=mp4]" silently picks a mute video.
        // Ranking h264 above h265 selects the streams that really have sound, and
        // h264 also plays inline on more Telegram clients. Format filters can't
        // catch this: [acodec!=none] trusts the same lying metadata.
        opts.FormatSort = "vcodec:h264";
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

    private async Task<RunResult<T>> WithTransientRetryAsync<T>(
        Uri url,
        Func<CancellationToken, Task<RunResult<T>>> operation,
        CancellationToken cancellationToken)
    {
        var pipeline = new ResiliencePipelineBuilder<RunResult<T>>()
            .AddRetry(new RetryStrategyOptions<RunResult<T>>
            {
                ShouldHandle = new PredicateBuilder<RunResult<T>>()
                    .HandleResult(r => !r.Success && YtDlpErrorClassifier.IsTransientChallengeFailure(JoinErrors(r.ErrorOutput))),
                // Measured ~50% success per attempt against a challenged TikTok URL (yt-dlp
                // 2026.07.04), and attempts look independent — so 10 total tries lands around
                // 99.9%. Metadata fetch and download each roll the challenge separately, which
                // compounds to ~99.8% end-to-end. Raising the cap is nearly free: the expected
                // attempt count stays 2, so a typical link still lands in seconds — the cap only
                // bounds the tail, at ~4s per attempt.
                MaxRetryAttempts = 9,
                BackoffType = DelayBackoffType.Constant,
                Delay = TimeSpan.FromMilliseconds(500),
                UseJitter = true,
                OnRetry = args =>
                {
                    _logger.LogWarning(
                        "Retrying yt-dlp call for {Url} after transient TikTok challenge failure (attempt {Attempt} after {DelayMs}ms)",
                        url,
                        args.AttemptNumber + 1,
                        args.RetryDelay.TotalMilliseconds);
                    return ValueTask.CompletedTask;
                },
            })
            .Build();

        return await pipeline.ExecuteAsync(async token => await operation(token), cancellationToken);
    }

    private async Task<Result<MediaPayload, ExtractionError>> DownloadPlaylistAsync(
        Uri url,
        string sanitisedUrl,
        YoutubeDLSharp.Metadata.VideoData info,
        OptionSet optionSet,
        CancellationToken cancellationToken)
    {
        var playlistResult = await WithTransientRetryAsync(
            url,
            token => _ytdl.RunVideoPlaylistDownload(
                sanitisedUrl,
                format: "best[ext=mp4]/best",
                ct: token,
                overrideOptions: optionSet),
            cancellationToken);

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
