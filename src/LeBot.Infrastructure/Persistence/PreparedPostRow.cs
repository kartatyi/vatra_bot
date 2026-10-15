using LeBot.Domain.Media;

namespace LeBot.Infrastructure.Persistence;

/// <summary>
/// Storage shape of an inline <see cref="Application.UseCases.AnswerInlineQuery.PreparedPost"/>, keyed by
/// the media cache's key digest for the link.
/// </summary>
internal sealed class PreparedPostRow
{
    public required string LinkKey { get; init; }

    public MediaKind? Kind { get; init; }

    public string? FileId { get; init; }

    public string? Body { get; init; }

    public required DateTimeOffset PreparedAt { get; init; }
}
