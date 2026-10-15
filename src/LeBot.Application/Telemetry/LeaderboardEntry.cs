namespace LeBot.Application.Telemetry;

/// <summary>One person's line in a chat's <c>/leaders</c> ranking.</summary>
/// <param name="SenderId">Telegram user id; the caller resolves it to a current name.</param>
/// <param name="Reposts">Links of theirs the bot reposted in the window.</param>
/// <param name="TopPlatform">The platform most of those came from (see <see cref="PlatformName"/>).</param>
/// <param name="TopPlatformReposts">How many came from <paramref name="TopPlatform"/>.</param>
public sealed record LeaderboardEntry(long SenderId, int Reposts, string TopPlatform, int TopPlatformReposts);
