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
}
