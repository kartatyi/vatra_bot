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

    /// <summary>
    /// Sets the bot's reaction on the message <paramref name="messageId"/>, replacing any it set before;
    /// <see langword="null"/> takes it off. A message carries one bot reaction at a time, so callers
    /// settle on the final one. Best-effort: when the chat doesn't allow the reaction, nothing shows
    /// and nothing throws.
    /// </summary>
    Task SetReactionAsync(long chatId, int messageId, LinkReaction? reaction, CancellationToken cancellationToken);
}

/// <summary>What the bot's reaction on a message with a link says.</summary>
public enum LinkReaction
{
    /// <summary>Seen it, working on it — while extraction and upload run.</summary>
    Working,

    /// <summary>This chat has seen the link before.</summary>
    Repeat,
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
