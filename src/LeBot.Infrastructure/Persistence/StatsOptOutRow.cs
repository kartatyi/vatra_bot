namespace LeBot.Infrastructure.Persistence;

/// <summary>One person who asked to be left out of one chat's <c>/leaders</c> ranking.</summary>
internal sealed class StatsOptOutRow
{
    public required string ChatHash { get; init; }

    public required long UserId { get; init; }
}
