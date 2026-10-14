using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using LeBot.Domain.Common;
using LeBot.Domain.Media;
using LeBot.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeBot.Infrastructure.MediaProcessing;

/// <summary>
/// Shrinks a video with ffmpeg: ffprobe reads its length, <see cref="CompressionPlan"/> turns that into
/// a bitrate that fits the cap, and the GPU — else libx264 — re-encodes at it. Both tools are looked for where yt-dlp
/// already finds ffmpeg — <see cref="YtDlpOptions.FfmpegPath"/>, else beside the yt-dlp binary. A missing
/// tool fails the compression, never the repost: the caller falls back to a text reply.
/// </summary>
internal sealed class FfmpegVideoCompressor(
    IOptions<YtDlpOptions> options,
    ILogger<FfmpegVideoCompressor> logger) : IVideoCompressor
{
    /// <summary>
    /// Updates are handled one at a time, so an encode holds up every chat behind it. On the PC the bot
    /// runs on, veryfast encodes 720p at ~8× real time; this bound still covers several minutes of
    /// 1080p, and the plan's bitrate floor already turns away anything much longer.
    /// </summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Tried in order. NVENC encodes on the GPU in a second or two where libx264 spends ~10 s of CPU,
    /// but it needs an NVIDIA card, a driver new enough for this ffmpeg build and a free encoder
    /// session. Missing any of them it refuses within a fraction of a second, and libx264 — built into
    /// every ffmpeg — takes over.
    /// </summary>
    internal static readonly IReadOnlyList<string> VideoEncoders = ["h264_nvenc", "libx264"];

    private readonly string _ffmpegPath = ResolveFfmpegPath(options.Value);

    public async Task<Result<string, ExtractionError>> CompressAsync(
        string sourcePath,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        var outputPath = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ?? Path.GetTempPath(),
            Path.GetFileNameWithoutExtension(sourcePath) + ".fit.mp4");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        try
        {
            return await CompressCoreAsync(sourcePath, outputPath, maxBytes, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            BestEffortDelete(outputPath);
            return Failure($"ffmpeg did not finish within {Timeout.TotalSeconds:0}s");
        }
        catch (OperationCanceledException)
        {
            BestEffortDelete(outputPath);
            throw;
        }
    }

    private async Task<Result<string, ExtractionError>> CompressCoreAsync(
        string sourcePath,
        string outputPath,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        var probe = await RunAsync(SiblingTool(_ffmpegPath, "ffprobe"), BuildProbeArguments(sourcePath), cancellationToken);
        if (probe.ExitCode != 0)
        {
            return Failure($"ffprobe failed: {probe.Detail}");
        }

        if (ParseProbe(probe.StandardOutput) is not { } media)
        {
            return Failure("ffprobe reported no duration");
        }

        if (CompressionPlan.For(media.DurationSeconds, media.HasAudio, maxBytes) is not { } plan)
        {
            return Failure(
                $"{media.DurationSeconds:0}s is too long to fit {maxBytes / (1024 * 1024)}MB at a watchable bitrate");
        }

        var failure = string.Empty;
        foreach (var encoder in VideoEncoders)
        {
            var encode = await RunAsync(
                _ffmpegPath, BuildEncodeArguments(sourcePath, outputPath, plan, encoder), cancellationToken);

            var output = new FileInfo(outputPath);
            if (encode.ExitCode == 0 && output.Exists && output.Length <= maxBytes)
            {
                logger.LogInformation(
                    "Re-encoded {Path} ({DurationSeconds}s) with {Encoder} at {VideoKbps}kbps, long side up to {MaxLongSide}px",
                    sourcePath, (int)media.DurationSeconds, encoder, plan.VideoKbps, plan.MaxLongSide);
                return Result<string, ExtractionError>.Success(outputPath);
            }

            BestEffortDelete(outputPath);
            failure = encode.ExitCode == 0 && output.Exists
                ? $"{encoder} still came out at {output.Length / (1024 * 1024)}MB"
                : $"{encoder} failed: {encode.Summary}";

            if (encoder != VideoEncoders[^1])
            {
                logger.LogWarning("Falling back from {Encoder} for {Path}: {Detail}", encoder, sourcePath, failure);
            }
        }

        return Failure(failure);
    }

    /// <summary>
    /// An explicit <see cref="YtDlpOptions.FfmpegPath"/> wins; otherwise the ffmpeg beside the yt-dlp
    /// binary, which is where yt-dlp itself finds it with no configuration.
    /// </summary>
    internal static string ResolveFfmpegPath(YtDlpOptions options) =>
        string.IsNullOrEmpty(options.FfmpegPath)
            ? SiblingTool(ExecutablePathResolver.Resolve(options.BinaryPath), "ffmpeg")
            : ExecutablePathResolver.Resolve(options.FfmpegPath);

    /// <summary>
    /// The tool called <paramref name="name"/> in the same folder as <paramref name="toolPath"/>, with
    /// the same extension — <c>yt-dlp.exe</c> beside <c>ffmpeg.exe</c>, <c>ffmpeg</c> beside
    /// <c>ffprobe</c>. A bare name stays bare, so the OS searches PATH for it.
    /// </summary>
    internal static string SiblingTool(string toolPath, string name)
    {
        var fileName = name + Path.GetExtension(toolPath);
        var directory = Path.GetDirectoryName(toolPath);
        return string.IsNullOrEmpty(directory) ? fileName : Path.Combine(directory, fileName);
    }

    internal static IReadOnlyList<string> BuildProbeArguments(string sourcePath) =>
        ["-v", "error", "-show_entries", "format=duration:stream=codec_type", "-of", "json", sourcePath];

    /// <summary>
    /// Reads the length and whether there is a sound track out of ffprobe's JSON. <c>null</c> when the
    /// length is missing: yt-dlp leaves it out of Instagram's metadata, and without it there is no
    /// bitrate to aim for.
    /// </summary>
    internal static (double DurationSeconds, bool HasAudio)? ParseProbe(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (!root.TryGetProperty("format", out var format)
                || !format.TryGetProperty("duration", out var duration)
                || duration.ValueKind != JsonValueKind.String
                || !double.TryParse(duration.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                || seconds <= 0)
            {
                return null;
            }

            var hasAudio = root.TryGetProperty("streams", out var streams)
                && streams.ValueKind == JsonValueKind.Array
                && streams.EnumerateArray().Any(stream =>
                    stream.TryGetProperty("codec_type", out var type) && type.ValueEquals("audio"));

            return (seconds, hasAudio);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// h264 + AAC in mp4 is what every Telegram client plays inline. yuv420p because a 10-bit source
    /// would otherwise come out as High 10, which phones refuse; faststart so playback begins before
    /// the whole file has arrived. The scale box caps the longer side whatever the orientation and
    /// never enlarges a smaller picture.
    /// </summary>
    internal static IReadOnlyList<string> BuildEncodeArguments(
        string sourcePath,
        string outputPath,
        CompressionPlan plan,
        string encoder)
    {
        var side = plan.MaxLongSide.ToString(CultureInfo.InvariantCulture);
        var videoKbps = plan.VideoKbps.ToString(CultureInfo.InvariantCulture);

        List<string> arguments =
        [
            "-hide_banner", "-nostdin", "-v", "error", "-y",
            "-i", sourcePath,
            "-map", "0:v:0", "-map", "0:a:0?",
            "-vf", $"scale='min(iw,{side})':'min(ih,{side})':force_original_aspect_ratio=decrease:force_divisible_by=2",
            .. EncoderSettings(encoder),
            "-pix_fmt", "yuv420p",
            "-b:v", $"{videoKbps}k", "-maxrate", $"{videoKbps}k",
            "-bufsize", $"{(plan.VideoKbps * 2).ToString(CultureInfo.InvariantCulture)}k",
        ];

        arguments.AddRange(plan.AudioKbps > 0
            ? ["-c:a", "aac", "-b:a", $"{plan.AudioKbps.ToString(CultureInfo.InvariantCulture)}k"]
            : ["-an"]);

        arguments.AddRange(["-movflags", "+faststart", outputPath]);
        return arguments;
    }

    /// <summary>
    /// p5 is NVENC's "slow, good quality" preset — still far faster than real time — and VBR capped at
    /// the target rate mirrors libx264's ABR, so both land at the size the plan expects.
    /// </summary>
    private static IReadOnlyList<string> EncoderSettings(string encoder) => encoder switch
    {
        "h264_nvenc" => ["-c:v", "h264_nvenc", "-preset", "p5", "-tune", "hq", "-rc", "vbr"],
        "libx264" => ["-c:v", "libx264", "-preset", "veryfast"],
        _ => throw new ArgumentOutOfRangeException(nameof(encoder), encoder, "No settings for this encoder."),
    };

    private static async Task<ToolRun> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            // The binary is not where the path says — the process never started.
            return new ToolRun(-1, string.Empty, $"could not start {executable}: {ex.Message}");
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // WaitForExitAsync stops waiting but leaves the encoder running, still writing the file.
            KillQuietly(process);
            throw;
        }

        return new ToolRun(process.ExitCode, await standardOutput, await standardError);
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(TimeSpan.FromSeconds(5));
        }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
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

    private static Result<string, ExtractionError> Failure(string detail) =>
        Result<string, ExtractionError>.Failure(new ExtractionError.ToolFailure(detail));

    private sealed record ToolRun(int ExitCode, string StandardOutput, string StandardError)
    {
        public string Detail => string.IsNullOrWhiteSpace(StandardError)
            ? $"exit code {ExitCode}"
            : StandardError.Trim();

        /// <summary>ffmpeg's first error line, which names the cause; the rest is the fallout.</summary>
        public string Summary => Detail.Split('\n', 2)[0].Trim();
    }
}
