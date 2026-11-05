namespace LeBot.Application.Telemetry;

/// <summary>
/// Who has asked to be left out of a chat's <c>/leaders</c> ranking. Opting out is per chat and hides
/// the person from the ranking; it doesn't rewrite the journal.
/// </summary>
public interface IStatsOptOutStore
{
    /// <summary>Records that <paramref name="userId"/> is out of (or back in) the ranking of <paramref name="chatId"/>. Idempotent.</summary>
    Task SetOptedOutAsync(long chatId, long userId, bool optedOut, CancellationToken cancellationToken);

    /// <summary>Whether <paramref name="userId"/> is out of the ranking of <paramref name="chatId"/>.</summary>
    Task<bool> IsOptedOutAsync(long chatId, long userId, CancellationToken cancellationToken);

    /// <summary>Everyone out of the ranking of <paramref name="chatId"/>.</summary>
    Task<IReadOnlySet<long>> GetOptedOutAsync(long chatId, CancellationToken cancellationToken);
}
