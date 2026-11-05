namespace LeBot.Application.Telemetry;

/// <summary>One person's <c>/me</c> card for a chat and a window.</summary>
/// <param name="Reposts">Links of theirs the bot reposted.</param>
/// <param name="Rank">Their place in the chat's ranking, 1-based.</param>
/// <param name="RankedPeople">How many people the ranking holds.</param>
/// <param name="TopPlatform">The platform most of their links came from.</param>
/// <param name="TopPlatformReposts">How many came from <paramref name="TopPlatform"/>.</param>
/// <param name="PeakHour">The local hour (0–23) they post most in.</param>
/// <param name="Trendsets">Links they brought first that others posted again later.</param>
/// <param name="Repeats">Links they posted that the chat had already seen.</param>
public sealed record PersonalStats(
    int Reposts,
    int Rank,
    int RankedPeople,
    string TopPlatform,
    int TopPlatformReposts,
    int PeakHour,
    int Trendsets,
    int Repeats);
