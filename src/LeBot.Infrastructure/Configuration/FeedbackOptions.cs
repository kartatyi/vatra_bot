namespace LeBot.Infrastructure.Configuration;

/// <summary>Where <c>/feedback</c> messages go (<c>Feedback</c> section).</summary>
public sealed class FeedbackOptions
{
    public const string SectionName = "Feedback";

    /// <summary>
    /// The maintainer's chat — deployment identity, so user-secrets or <c>appsettings.Local.json</c> only.
    /// Unset, feedback goes to <see cref="UpdateOptions.NotifyChatId"/>, the operator chat the bot already
    /// writes to; with neither, <c>/feedback</c> says it isn't set up.
    /// </summary>
    public long? ChatId { get; init; }

    /// <summary>How long one person waits between two pieces of feedback — a guard against floods, not a judgement.</summary>
    public int CooldownSeconds { get; init; } = 60;
}
