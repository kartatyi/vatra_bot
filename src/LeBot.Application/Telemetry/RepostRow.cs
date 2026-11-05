namespace LeBot.Application.Telemetry;

/// <summary>One successful repost, as little as the weekly digest needs of it.</summary>
/// <param name="SenderId">Who posted the link, when a person did.</param>
/// <param name="Host">The link's lower-cased host.</param>
public sealed record RepostRow(long? SenderId, string Host);
