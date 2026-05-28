using LeBot.Domain.Media;

namespace LeBot.Application.Ports;

/// <summary>
/// The boundary between the application core and whichever Telegram client we use.
/// The application doesn't know about <c>Telegram.Bot</c>; it just asks for a reply.
/// </summary>
public interface ITelegramMessenger
{
    /// <summary>Posts the media items in the payload as a reply (caption holds the payload's body text).</summary>
    Task ReplyWithMediaAsync(
        long chatId,
        int replyToMessageId,
        MediaPayload payload,
        CancellationToken cancellationToken);

    /// <summary>Posts the payload's body text (description / title / author) as a text-only reply.</summary>
    Task ReplyWithTextAsync(
        long chatId,
        int replyToMessageId,
        MediaPayload payload,
        CancellationToken cancellationToken);

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
