using System.Collections.Concurrent;
using LeBot.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace LeBot.Infrastructure.Telegram;

/// <summary>
/// <c>/feedback &lt;text&gt;</c>: a bug report, a wish, a thank-you — relayed to the maintainer's chat with
/// who sent it and from where, and acknowledged to the sender. One person can send once per cooldown.
/// </summary>
public sealed class FeedbackRelay(
    ITelegramBotClient bot,
    IOptions<FeedbackOptions> feedback,
    IOptions<UpdateOptions> update,
    TimeProvider timeProvider,
    ILogger<FeedbackRelay> logger)
{
    public const string Usage = "Tell me what's broken or what you'd like to see: /feedback <your message>";
    public const string Thanks = "Thanks — sent to the maintainer 🙏";
    public const string NotSetUp = "Feedback isn't set up on this bot yet.";
    public const string TooSoon = "Got your last one — give it a minute before the next.";
    public const string Failed = "Couldn't deliver that just now — please try again later.";

    private const int MaxLength = 3500; // leaves room for the header inside Telegram's 4096

    private readonly ConcurrentDictionary<long, DateTimeOffset> _lastSent = new();

    /// <summary>Relays <paramref name="text"/> from <paramref name="message"/>'s sender and returns the reply for them.</summary>
    public async Task<string> RelayAsync(Message message, string? text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Usage;
        }

        if ((feedback.Value.ChatId ?? update.Value.NotifyChatId) is not { } maintainer)
        {
            return NotSetUp;
        }

        var senderId = message.From?.Id ?? message.Chat.Id;
        var now = timeProvider.GetUtcNow();
        var cooldown = TimeSpan.FromSeconds(feedback.Value.CooldownSeconds);
        if (_lastSent.TryGetValue(senderId, out var last) && now - last < cooldown)
        {
            return TooSoon;
        }

        try
        {
            await bot.SendMessage(maintainer, Format(message, text.Trim()), cancellationToken: cancellationToken);
            _lastSent[senderId] = now;
            logger.LogInformation("Relayed feedback from chat {ChatId}", message.Chat.Id);
            return Thanks;
        }
        catch (RequestException ex)
        {
            logger.LogWarning(ex, "Could not relay feedback from chat {ChatId}", message.Chat.Id);
            return Failed;
        }
    }

    /// <summary>The maintainer's copy: who, from where, then the text — plain, so nothing in it can format.</summary>
    internal static string Format(Message message, string text)
    {
        var from = message.From is { } user
            ? (string.IsNullOrWhiteSpace(user.LastName) ? user.FirstName : $"{user.FirstName} {user.LastName}")
              + (user.Username is { Length: > 0 } handle ? $" (@{handle})" : string.Empty)
            : "someone";
        var where = message.Chat.Type == ChatType.Private ? "a private chat" : $"«{message.Chat.Title}»";
        var body = text.Length > MaxLength ? text[..(MaxLength - 1)] + "…" : text;
        return $"💬 Feedback from {from} in {where}:\n\n{body}";
    }
}
