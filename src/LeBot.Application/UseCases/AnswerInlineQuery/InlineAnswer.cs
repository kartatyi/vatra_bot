namespace LeBot.Application.UseCases.AnswerInlineQuery;

/// <summary>What the bot offers in reply to one inline query. The adapter turns each case into Telegram results.</summary>
public abstract record InlineAnswer
{
    private InlineAnswer()
    {
    }

    /// <summary>The query holds no link yet — nothing to offer.</summary>
    public sealed record NoLink : InlineAnswer;

    /// <summary>The post is ready to send.</summary>
    public sealed record Ready(PreparedPost Post) : InlineAnswer;

    /// <summary>The post is still being fetched; the user can send the bare link meanwhile.</summary>
    public sealed record NotReady(Uri Url) : InlineAnswer;

    /// <summary>Nothing could be fetched from the link; the user can still send it as a link.</summary>
    public sealed record Failed(Uri Url) : InlineAnswer;
}
