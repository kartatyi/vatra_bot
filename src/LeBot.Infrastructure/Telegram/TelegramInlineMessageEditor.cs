using LeBot.Application.Ports;
using LeBot.Application.UseCases.AnswerInlineQuery;
using LeBot.Domain.Media;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;

namespace LeBot.Infrastructure.Telegram;

/// <summary>
/// <see cref="IInlineMessageEditor"/> over the Bot API. An inline message can only take media Telegram
/// already holds, which is exactly what a <see cref="PreparedPost"/> carries — its file id.
/// </summary>
internal sealed class TelegramInlineMessageEditor(
    ITelegramBotClient bot,
    ILogger<TelegramInlineMessageEditor> logger) : IInlineMessageEditor
{
    public async Task ShowPostAsync(string inlineMessageId, PreparedPost post, bool withText, Uri url, string? languageCode, CancellationToken cancellationToken)
    {
        // The "Original" button stays: an inline message lands in someone else's chat, where the link to the
        // source — a listing, a thread — is often the point.
        var original = InlineResults.OriginalLink(url, BotLanguages.From(languageCode));
        try
        {
            if (post is { Kind: { } kind, FileId: { } fileId })
            {
                var caption = withText && post.Body is { } body ? InlineResults.Fit(body, InlineResults.CaptionLimit) : null;
                await bot.EditMessageMedia(inlineMessageId, Media(kind, fileId, caption), replyMarkup: original, cancellationToken: cancellationToken);
            }
            else if (post.Body is { } text)
            {
                // A text-only post is its text, whichever variant was picked.
                await bot.EditMessageText(inlineMessageId, InlineResults.Fit(text, InlineResults.TextLimit), replyMarkup: original, cancellationToken: cancellationToken);
            }
        }
        catch (RequestException ex)
        {
            // The sender may have deleted the placeholder already; nothing left to fix.
            logger.LogDebug(ex, "Could not turn an inline placeholder into its post");
        }
    }

    public async Task ShowFailureAsync(string inlineMessageId, Uri url, string? languageCode, CancellationToken cancellationToken)
    {
        try
        {
            await bot.EditMessageText(
                inlineMessageId,
                $"{InlineResults.FailedTitle(BotLanguages.From(languageCode))}\n{url.AbsoluteUri}",
                replyMarkup: InlineResults.OriginalLink(url, BotLanguages.From(languageCode)),
                cancellationToken: cancellationToken);
        }
        catch (RequestException ex)
        {
            logger.LogDebug(ex, "Could not mark an inline placeholder as failed");
        }
    }

    private static InputMedia Media(MediaKind kind, string fileId, string? caption)
    {
        var file = InputFile.FromFileId(fileId);
        return kind switch
        {
            MediaKind.Photo => new InputMediaPhoto(file) { Caption = caption },
            MediaKind.Animation => new InputMediaAnimation(file) { Caption = caption },
            MediaKind.Audio => new InputMediaAudio(file) { Caption = caption },
            _ => new InputMediaVideo(file) { Caption = caption, SupportsStreaming = true },
        };
    }
}
