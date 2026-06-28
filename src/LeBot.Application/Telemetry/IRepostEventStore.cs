namespace LeBot.Application.Telemetry;

/// <summary>
/// Durable, append-only sink for <see cref="RepostEvent"/>s — the persistence port behind the
/// dashboard. The Infrastructure adapter owns the database; this layer only knows it can record an
/// event and (later) query rollups.
/// </summary>
public interface IRepostEventStore
{
    /// <summary>
    /// Persists one event. <b>Best-effort:</b> the adapter logs and swallows storage failures rather
    /// than throwing, because dropping a telemetry row must never break the repost the user actually
    /// asked for. Honour <paramref name="cancellationToken"/> for shutdown.
    /// </summary>
    Task AppendAsync(RepostEvent repostEvent, CancellationToken cancellationToken);
}
