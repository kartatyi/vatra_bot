namespace LeBot.Infrastructure.MediaExtraction.YtDlp;

/// <summary>
/// Bounds how long one yt-dlp call may run. Links are handled one at a time, so a call that never returns —
/// a live stream recorded "to the end", a server that stops answering — would otherwise stall every chat.
/// YoutubeDLSharp kills yt-dlp's whole process tree when its token is cancelled, ffmpeg included, so
/// running out of time leaves nothing behind.
/// </summary>
internal static class TimeBox
{
    /// <summary>
    /// The result of <paramref name="operation"/>, or <see langword="null"/> when it ran past
    /// <paramref name="limit"/>. Cancellation of <paramref name="cancellationToken"/> itself — shutdown —
    /// is not a timeout and propagates as usual.
    /// </summary>
    internal static async Task<T?> RunAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        TimeSpan limit,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
        where T : class
    {
        using var deadline = new CancellationTokenSource(limit, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            return await operation(linked.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}
