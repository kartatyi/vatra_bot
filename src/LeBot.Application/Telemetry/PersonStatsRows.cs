namespace LeBot.Application.Telemetry;

/// <summary>When one person's link was reposted — the raw row behind the 🦉 badge and <c>/me</c>'s busiest hour.</summary>
/// <param name="SenderId">Telegram user id of the person who posted the link.</param>
/// <param name="At">When the repost happened.</param>
public sealed record SenderMoment(long SenderId, DateTimeOffset At);

/// <summary>A per-person tally, such as repeats posted or links others re-posted after them.</summary>
/// <param name="SenderId">Telegram user id.</param>
/// <param name="Count">The tally.</param>
public sealed record SenderCount(long SenderId, int Count);
