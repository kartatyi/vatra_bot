using LeBot.Domain.Media;

namespace LeBot.Application.Ports;

/// <summary>
/// The boundary between the application core and whichever Telegram client we use.
/// The application doesn't know about <c>Telegram.Bot</c>; it just asks for a reply.
/// </summary>
public interface ITelegramMessenger
{
    /// <summary>
    /// Posts the media items in the payload as a reply (caption holds the payload's body text). When the
    /// payload has <see cref="MediaPayload.RevealableText"/>, a single-message reply carries a button that
    /// shows it. Returns that message's id, or <see langword="null"/> when the reply went out as an album
    /// (which can't carry a button) or fell back to a plain link.
    /// </summary>
    Task<int?> ReplyWithMediaAsync(
        long chatId,
        int replyToMessageId,
        MediaPayload payload,
        CancellationToken cancellationToken);

    /// <summary>
    /// Posts the payload's body text (description / title / author) as a text-only reply, with the same
    /// <see cref="MediaPayload.RevealableText"/> button. Returns the message id, or <see langword="null"/>
    /// when there was no text to send.
    /// </summary>
    Task<int?> ReplyWithTextAsync(
        long chatId,
        int replyToMessageId,
        MediaPayload payload,
        CancellationToken cancellationToken);

    /// <summary>
    /// Sends a standalone text message to a chat (not a reply to any message). Used for operator
    /// notifications such as self-update progress. Over-long text is trimmed to Telegram's limit.
    /// </summary>
    Task SendTextAsync(long chatId, string text, CancellationToken cancellationToken);

    /// <summary>
    /// Shows the chat header status ("Bot is uploading a video...", etc.) and keeps it alive
    /// until the returned handle is disposed. The implementation re-sends the action periodically
    /// because Telegram dismisses each call after ~5 seconds.
    /// </summary>
    IAsyncDisposable IndicateBusy(long chatId, BusyKind kind);
}

/// <summary>
/// The visible status the chat header shows while the bot is working on something.
/// Mirrors the Telegram Bot API's chat-action vocabulary in a transport-agnostic way.
/// </summary>
public enum BusyKind
{
    UploadingVideo,
    UploadingPhoto,
    Typing,
}
