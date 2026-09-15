using System.ComponentModel;
using LeBot.Domain.Common;
using LeBot.Domain.Media;
using LeBot.Infrastructure.Configuration;
using Microsoft.Extensions.Options;
using YoutubeDLSharp;
using YoutubeDLSharp.Options;

namespace LeBot.Infrastructure.MediaExtraction.Steam;

/// <summary>
/// Downloads a trailer rendition with yt-dlp, which fetches the DASH segments of both streams and has
/// ffmpeg merge them. ffmpeg has to be reachable — beside the yt-dlp binary, where yt-dlp finds it
/// unaided, or at <see cref="YtDlpOptions.FfmpegPath"/>. Without it the merge fails and the extractor
/// posts the screenshots alone.
/// </summary>
internal sealed class YtDlpSteamTrailerDownloader : ISteamTrailerDownloader
{
    private readonly YoutubeDL _ytdl;
    private readonly string _downloadDirectory;

    public YtDlpSteamTrailerDownloader(IOptions<YtDlpOptions> options)
    {
        var ytDlp = options.Value;
        _downloadDirectory = ytDlp.ResolvedDownloadDirectory;
        Directory.CreateDirectory(_downloadDirectory);

        _ytdl = new YoutubeDL
        {
            YoutubeDLPath = ExecutablePathResolver.Resolve(ytDlp.BinaryPath),
            OutputFolder = _downloadDirectory,
        };

        if (!string.IsNullOrEmpty(ytDlp.FfmpegPath))
        {
            _ytdl.FFmpegPath = ExecutablePathResolver.Resolve(ytDlp.FfmpegPath);
        }
    }

    public async Task<Result<string, ExtractionError>> DownloadAsync(
        Uri manifestUrl,
        int height,
        CancellationToken cancellationToken)
    {
        var options = new OptionSet
        {
            // Every Steam manifest is called dash_h264.mpd, so yt-dlp's default title-based name would
            // make two trailers downloading at once write the same file.
            Output = Path.Combine(_downloadDirectory, $"steam_{Guid.NewGuid():N}.%(ext)s"),
        };

        RunResult<string> download;
        try
        {
            // Height is the one thing the manifest and yt-dlp's format list agree on: yt-dlp numbers
            // the formats of a bare manifest its own way.
            download = await _ytdl.RunVideoDownload(
                manifestUrl.AbsoluteUri,
                format: $"bv*[height={height}]+ba/b[height={height}]",
                mergeFormat: DownloadMergeFormat.Mp4,
                ct: cancellationToken,
                overrideOptions: options);
        }
        catch (Win32Exception ex)
        {
            // The binary isn't where YtDlp:BinaryPath says — the process never started.
            return Result<string, ExtractionError>.Failure(new ExtractionError.ToolFailure(ex.Message));
        }

        if (!download.Success || string.IsNullOrEmpty(download.Data))
        {
            var detail = string.Join("; ", download.ErrorOutput);
            return Result<string, ExtractionError>.Failure(
                new ExtractionError.ToolFailure(string.IsNullOrWhiteSpace(detail) ? "no error output" : detail));
        }

        return File.Exists(download.Data)
            ? Result<string, ExtractionError>.Success(download.Data)
            : Result<string, ExtractionError>.Failure(
                new ExtractionError.ToolFailure($"output file missing: {download.Data}"));
    }
}
