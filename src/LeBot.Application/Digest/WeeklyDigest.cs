namespace LeBot.Application.Digest;

/// <summary>One chat's summary of a week of reposts — what the Monday-morning post says.</summary>
/// <param name="WeekStart">The week's first day (Monday), local.</param>
/// <param name="WeekEnd">The week's last day (Sunday), local.</param>
/// <param name="Reposts">Links reposted in the week.</param>
/// <param name="Change">Reposts compared with the week before: positive is up.</param>
/// <param name="TopPlatform">The platform most links came from.</param>
/// <param name="TopPlatformReposts">How many came from <paramref name="TopPlatform"/>.</param>
/// <param name="TopDroppers">Up to three people with the most reposts, best first; empty when no sender is known.</param>
public sealed record WeeklyDigest(
    DateOnly WeekStart,
    DateOnly WeekEnd,
    int Reposts,
    int Change,
    string TopPlatform,
    int TopPlatformReposts,
    IReadOnlyList<(long SenderId, int Reposts)> TopDroppers);
