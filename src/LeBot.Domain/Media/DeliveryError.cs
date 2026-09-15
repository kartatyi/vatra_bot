namespace LeBot.Domain.Media;

/// <summary>
/// Reasons media that was extracted fine still never reached the chat. The counterpart of
/// <see cref="ExtractionError"/> for the last leg: the platform gave up the bytes, Telegram did not
/// take them. Pattern-match on the concrete variant to tell a refusal from a lost connection.
/// </summary>
public abstract record DeliveryError(string Reason)
{
    /// <summary>
    /// Telegram answered and said no — a permanent <c>4xx</c> (file too big, reply target deleted), or a
    /// <c>429</c>/<c>5xx</c> that outlasted every retry.
    /// </summary>
    public sealed record TelegramRefused(int ErrorCode, string Detail)
        : DeliveryError($"Telegram refused the upload ({ErrorCode}): {Detail}");

    /// <summary>
    /// No answer came back — connection reset, DNS or TLS failure, timeout — even after retries.
    /// Nothing about the media itself is known to be wrong, so a later attempt may well succeed.
    /// </summary>
    public sealed record TelegramUnreachable(string Detail)
        : DeliveryError($"Telegram unreachable during upload: {Detail}");
}
