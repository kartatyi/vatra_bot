namespace LeBot.Application.UseCases.HandleIncomingMessage;

/// <summary>
/// The bot-agnostic shape of a single chat message the dispatcher hands to the handler.
/// Decoupled from <c>Telegram.Bot.Types.Message</c> so the use-case stays testable without
/// the Telegram SDK in the picture.
/// </summary>
/// <param name="ChatId">Telegram chat id (negative for groups, positive for users).</param>
/// <param name="MessageId">Id of the original message; used to reply.</param>
/// <param name="Text">The text payload; empty when the message had none.</param>
/// <param name="SenderUsername">Sender's Telegram username, or <c>&lt;unknown&gt;</c> when missing.</param>
public sealed record IncomingMessage(
    long ChatId,
    int MessageId,
    string Text,
    string SenderUsername);
