using System.Collections.Concurrent;

namespace LeBot.Application.Metrics;

/// <summary>
/// In-memory counters for what the bot has done since process start.
/// Read on demand by the <c>/stats</c> command and logged at info level by
/// the dispatcher's idle heartbeat. Thread-safe via <see cref="Interlocked"/>
/// and <see cref="ConcurrentDictionary{TKey,TValue}"/>; intentionally not persisted
/// — restarting the process resets to zero, which is the right behaviour for a
/// "how is this build doing" diagnostic rather than long-term analytics.
/// </summary>
public sealed class RepostMetrics(TimeProvider timeProvider)
{
    private long _mediaReposts;
    private long _textReposts;
    private long _failures;
    private long _silentSkips;
    private long _cacheHits;

    private readonly ConcurrentDictionary<string, long> _byExtractor = new();

    public DateTimeOffset StartedAt { get; } = timeProvider.GetUtcNow();
    public long MediaReposts => Interlocked.Read(ref _mediaReposts);
    public long TextReposts => Interlocked.Read(ref _textReposts);
    public long Failures => Interlocked.Read(ref _failures);
    public long SilentSkips => Interlocked.Read(ref _silentSkips);

    /// <summary>
    /// Reposts served from the media cache. Counted <em>on top of</em> the media/text counters —
    /// a cache hit is still a repost — so this reads as "of which, without touching the network".
    /// </summary>
    public long CacheHits => Interlocked.Read(ref _cacheHits);
    public IReadOnlyDictionary<string, long> ByExtractor => _byExtractor;

    public void RecordMediaRepost(string extractor)
    {
        Interlocked.Increment(ref _mediaReposts);
        Bump(extractor);
    }

    public void RecordTextRepost(string? extractor = null)
    {
        Interlocked.Increment(ref _textReposts);
        if (!string.IsNullOrEmpty(extractor))
        {
            Bump(extractor);
        }
    }

    public void RecordFailure(string extractor)
    {
        Interlocked.Increment(ref _failures);
        Bump(extractor);
    }

    public void RecordSilentSkip() => Interlocked.Increment(ref _silentSkips);

    public void RecordCacheHit() => Interlocked.Increment(ref _cacheHits);

    private void Bump(string extractor) =>
        _byExtractor.AddOrUpdate(extractor, 1, static (_, current) => current + 1);
}
