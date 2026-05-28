using LeBot.Domain.Media;

namespace LeBot.Application.Ports;

/// <summary>
/// The boundary between the application core and whichever Telegram client we use.
/// The application doesn't know about <c>Telegram.Bot</c>; it just asks for a reply.
/// </summary>
public interface ITelegramMessenger
{
    /// <summary>Posts the payload as a reply to a specific message in a specific chat.</summary>
    Task ReplyWithMediaAsync(
        long chatId,
        int replyToMessageId,
        MediaPayload payload,
        CancellationToken cancellationToken);
}
