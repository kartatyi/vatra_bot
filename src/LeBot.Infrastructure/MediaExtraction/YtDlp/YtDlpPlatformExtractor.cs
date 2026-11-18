using System.Text.RegularExpressions;
using LeBot.Application.Ports;
using LeBot.Domain.Common;
using LeBot.Domain.Media;
using LeBot.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using YoutubeDLSharp;
using YoutubeDLSharp.Metadata;
using YoutubeDLSharp.Options;

namespace LeBot.Infrastructure.MediaExtraction.YtDlp;

/// <summary>
/// Universal media extractor backed by yt-dlp. Claims responsibility for a curated
/// list of hosts where the user is likely to post links; everything else falls
/// through so a future per-platform extractor can take over.
/// </summary>
public sealed partial class YtDlpPlatformExtractor : IPlatformExtractor
{
    /// <summary>
    /// The format a live clip is recorded in: 480p is all a round video needs, so the recording stays
    /// small and quick to re-encode. Live streams list their video and audio separately.
    /// </summary>
    internal const string LiveClipFormat = "bv*[height<=480]+ba/b[height<=480]/b";

    /// <summary>
    /// Time a live clip may take beyond its own length: yt-dlp's start-up and the stream's first segments.
    /// A 30 s clip measured ~25 s end to end — the stream's DVR window lets it read ahead of real time.
    /// </summary>
    private static readonly TimeSpan LiveClipSlack = TimeSpan.FromSeconds(90);

    /// <summary>Re-encoding a clip of up to a minute at 480p measured about a second; this only bounds a stuck ffmpeg.</summary>
    private static readonly TimeSpan VideoNoteEncodeTimeout = TimeSpan.FromSeconds(60);

    private readonly YoutubeDL _ytdl;
    private readonly YtDlpOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<YtDlpPlatformExtractor> _logger;
    private readonly string? _ffmpegPath;

    public YtDlpPlatformExtractor(
        IOptions<YtDlpOptions> options,
        TimeProvider timeProvider,
        ILogger<YtDlpPlatformExtractor> logger)
    {
        _options = options.Value;
        _timeProvider = timeProvider;
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

        _ffmpegPath = ResolveFfmpegPath(_options);
    }

    /// <summary>
    /// The ffmpeg a live clip is cut and encoded with: <see cref="YtDlpOptions.FfmpegPath"/> when set, else
    /// the ffmpeg beside the yt-dlp binary — where <c>tools/fetch-tools.ps1</c> puts it and where yt-dlp
    /// finds it unaided. Null when neither exists.
    /// </summary>
    internal static string? ResolveFfmpegPath(YtDlpOptions options)
    {
        if (!string.IsNullOrEmpty(options.FfmpegPath))
        {
            return ExecutablePathResolver.Resolve(options.FfmpegPath);
        }

        var beside = Path.Combine(
            Path.GetDirectoryName(ExecutablePathResolver.Resolve(options.BinaryPath)) ?? string.Empty,
            OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
        return File.Exists(beside) ? beside : null;
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
            var metadata = await TimeBox.RunAsync(
                token => WithTransientRetryAsync(
                    url,
                    innerToken => _ytdl.RunVideoDataFetch(sanitisedUrl, ct: innerToken, overrideOptions: optionSet),
                    token),
                TimeSpan.FromSeconds(_options.MetadataTimeoutSeconds),
                _timeProvider,
                cancellationToken);
            if (metadata is null)
            {
                return TimedOut(url, "metadata fetch", _options.MetadataTimeoutSeconds);
            }

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

            // A live stream never ends, so "download it" would never return. Record a short clip instead.
            if (IsLiveBroadcast(info))
            {
                return await RecordLiveClipAsync(url, sanitisedUrl, info, cancellationToken);
            }

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
            var download = await TimeBox.RunAsync(
                token => WithTransientRetryAsync(
                    url,
                    innerToken => _ytdl.RunVideoDownload(
                        sanitisedUrl,
                        format: "best[ext=mp4]/best",
                        ct: innerToken,
                        overrideOptions: optionSet),
                    token),
                TimeSpan.FromSeconds(_options.DownloadTimeoutSeconds),
                _timeProvider,
                cancellationToken);
            if (download is null)
            {
                return TimedOut(url, "download", _options.DownloadTimeoutSeconds);
            }

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

    /// <summary>
    /// True when the post is a broadcast that is on air right now. A finished stream (<c>was_live</c>) is an
    /// ordinary recording and an upcoming one has nothing to show yet; neither counts.
    /// </summary>
    internal static bool IsLiveBroadcast(VideoData info) =>
        info.IsLive == true || info.LiveStatus == LiveStatus.IsLive;

    /// <summary>
    /// The options a live clip is recorded with: ffmpeg reads the stream and stops after
    /// <paramref name="seconds"/> of output — from the moment it joins, not from the stream's start — which
    /// is what turns an endless download into a bounded one.
    /// </summary>
    internal OptionSet BuildLiveOptionSet(int seconds)
    {
        var opts = BuildOptionSet();
        opts.AddCustomOption("--downloader", "ffmpeg");
        opts.AddCustomOption("--downloader-args", $"ffmpeg_o:-t {seconds}");
        return opts;
    }

    /// <summary>
    /// Records <see cref="YtDlpOptions.LiveClipSeconds"/> of a live stream and turns them into a round video.
    /// Without ffmpeg there is no way to cut a stream, so the post's text is all that's left.
    /// </summary>
    private async Task<Result<MediaPayload, ExtractionError>> RecordLiveClipAsync(
        Uri url,
        string sanitisedUrl,
        VideoData info,
        CancellationToken cancellationToken)
    {
        if (_ffmpegPath is null)
        {
            _logger.LogInformation("Skipping the clip of live stream {Url}: no ffmpeg configured — surfacing text only", url);
            return Result<MediaPayload, ExtractionError>.Success(
                new MediaPayload(url, LiveTitle(info.Title), info.Uploader, [], info.Description) { IsLive = true });
        }

        var seconds = Math.Clamp(_options.LiveClipSeconds, 1, VideoNoteEncoder.MaxSeconds);
        var limit = TimeSpan.FromSeconds(seconds) + LiveClipSlack;

        var recording = await TimeBox.RunAsync(
            token => _ytdl.RunVideoDownload(
                sanitisedUrl,
                format: LiveClipFormat,
                ct: token,
                overrideOptions: BuildLiveOptionSet(seconds)),
            limit,
            _timeProvider,
            cancellationToken);
        if (recording is null)
        {
            return TimedOut(url, "live clip recording", (int)limit.TotalSeconds);
        }

        if (!recording.Success || string.IsNullOrEmpty(recording.Data) || !File.Exists(recording.Data))
        {
            var detail = JoinErrors(recording.ErrorOutput);
            _logger.LogWarning("Recording a clip of live stream {Url} failed: {Detail}", url, detail);
            return Result<MediaPayload, ExtractionError>.Failure(new ExtractionError.ToolFailure(detail));
        }

        var rawPath = recording.Data;
        var notePath = Path.ChangeExtension(rawPath, ".note.mp4");
        var encoded = await VideoNoteEncoder.EncodeAsync(
            _ffmpegPath, rawPath, notePath, seconds, VideoNoteEncodeTimeout, _timeProvider, _logger, cancellationToken);
        BestEffortDelete(rawPath);
        if (!encoded)
        {
            BestEffortDelete(notePath);
            return Result<MediaPayload, ExtractionError>.Failure(
                new ExtractionError.ToolFailure("could not turn the live clip into a round video"));
        }

        var item = new MediaItem(
            FilePath: notePath,
            Kind: MediaKind.VideoNote,
            MimeType: "video/mp4",
            SizeBytes: new FileInfo(notePath).Length,
            DurationSeconds: seconds);

        _logger.LogInformation("Recorded {Seconds}s of live stream {Url} as a round video", seconds, url);
        return Result<MediaPayload, ExtractionError>.Success(
            new MediaPayload(url, LiveTitle(info.Title), info.Uploader, [item], info.Description) { IsLive = true });
    }

    /// <summary>
    /// <paramref name="title"/> without the "YYYY-MM-DD HH:MM" yt-dlp appends to every live stream's title —
    /// the moment it looked, which says nothing a viewer needs and crowds the button's one line.
    /// </summary>
    internal static string? LiveTitle(string? title) =>
        title is null ? null : LiveTimestampSuffix().Replace(title, string.Empty);

    [GeneratedRegex(@"\s+\d{4}-\d{2}-\d{2} \d{2}:\d{2}$")]
    private static partial Regex LiveTimestampSuffix();

    private Result<MediaPayload, ExtractionError> TimedOut(Uri url, string step, int seconds)
    {
        _logger.LogWarning("yt-dlp {Step} for {Url} ran past {TimeoutSeconds}s; killed it", step, url, seconds);
        return Result<MediaPayload, ExtractionError>.Failure(
            new ExtractionError.ToolFailure($"yt-dlp {step} timed out after {seconds}s"));
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
        VideoData info,
        OptionSet optionSet,
        CancellationToken cancellationToken)
    {
        var playlistResult = await TimeBox.RunAsync(
            token => WithTransientRetryAsync(
                url,
                innerToken => _ytdl.RunVideoPlaylistDownload(
                    sanitisedUrl,
                    format: "best[ext=mp4]/best",
                    ct: innerToken,
                    overrideOptions: optionSet),
                token),
            TimeSpan.FromSeconds(_options.DownloadTimeoutSeconds),
            _timeProvider,
            cancellationToken);
        if (playlistResult is null)
        {
            return TimedOut(url, "playlist download", _options.DownloadTimeoutSeconds);
        }

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

    private static bool AllEntriesHaveNoFormats(VideoData[] entries)
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
