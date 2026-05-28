using LeBot.Application.Ports;
using LeBot.Domain.Media;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;

namespace LeBot.Infrastructure.Telegram;

/// <summary>
/// Adapter over <see cref="ITelegramBotClient"/>. Picks the right send-method for each
/// <see cref="MediaKind"/>, falls back to a plain source-URL reply when Telegram refuses
/// the upload, and always cleans the local file after the attempt.
/// </summary>
public sealed class TelegramBotMessenger(
    ITelegramBotClient bot,
    ILogger<TelegramBotMessenger> logger)
    : ITelegramMessenger
{
    public async Task ReplyWithMediaAsync(
        long chatId,
        int replyToMessageId,
        MediaPayload payload,
        CancellationToken cancellationToken)
    {
        var caption = BuildBody(payload, MaxCaptionLength);
        var reply = new ReplyParameters { MessageId = replyToMessageId };

        foreach (var item in payload.Items)
        {
            try
            {
                await SendItemAsync(chatId, reply, item, caption, cancellationToken);
            }
            catch (ApiRequestException ex)
            {
                logger.LogWarning(
                    ex,
                    "Telegram refused media (code {ErrorCode}): {Message}. Falling back to source link.",
                    ex.ErrorCode, ex.Message);
                await bot.SendMessage(
                    chatId: chatId,
                    text: $"Couldn't repost the media. Source: {payload.SourceUrl}",
                    replyParameters: reply,
                    cancellationToken: cancellationToken);
            }
            finally
            {
                BestEffortDelete(item.FilePath);
            }
        }
    }

    public async Task ReplyWithTextAsync(
        long chatId,
        int replyToMessageId,
        MediaPayload payload,
        CancellationToken cancellationToken)
    {
        var body = BuildBody(payload, MaxTextLength);
        if (string.IsNullOrEmpty(body))
        {
            return;
        }

        await bot.SendMessage(
            chatId: chatId,
            text: body,
            replyParameters: new ReplyParameters { MessageId = replyToMessageId },
            cancellationToken: cancellationToken);
    }

    private async Task SendItemAsync(
        long chatId,
        ReplyParameters reply,
        MediaItem item,
        string? caption,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(item.FilePath);
        var inputFile = InputFile.FromStream(stream, Path.GetFileName(item.FilePath));

        switch (item.Kind)
        {
            case MediaKind.Video:
                await bot.SendVideo(
                    chatId: chatId,
                    video: inputFile,
                    caption: caption,
                    replyParameters: reply,
                    cancellationToken: cancellationToken);
                break;
            case MediaKind.Photo:
                await bot.SendPhoto(
                    chatId: chatId,
                    photo: inputFile,
                    caption: caption,
                    replyParameters: reply,
                    cancellationToken: cancellationToken);
                break;
            case MediaKind.Animation:
                await bot.SendAnimation(
                    chatId: chatId,
                    animation: inputFile,
                    caption: caption,
                    replyParameters: reply,
                    cancellationToken: cancellationToken);
                break;
            case MediaKind.Audio:
                await bot.SendAudio(
                    chatId: chatId,
                    audio: inputFile,
                    caption: caption,
                    replyParameters: reply,
                    cancellationToken: cancellationToken);
                break;
        }
    }

    private void BestEffortDelete(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex, "Could not delete {Path}", filePath);
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogDebug(ex, "Could not delete {Path}", filePath);
        }
    }

    // Telegram caps captions on media messages at 1024 chars; standalone text messages at 4096.
    private const int MaxCaptionLength = 1024;
    private const int MaxTextLength = 4096;

    private static string? BuildBody(MediaPayload payload, int maxLength)
    {
        // Prefer the description (the actual post body — what the user wrote on IG, TikTok, etc.)
        // over the title (which yt-dlp often synthesises as "Video by <uploader>" for short-form).
        var primary = !string.IsNullOrWhiteSpace(payload.Description)
            ? payload.Description.Trim()
            : !string.IsNullOrWhiteSpace(payload.Title)
                ? payload.Title.Trim()
                : null;

        var author = !string.IsNullOrWhiteSpace(payload.Author)
            ? payload.Author.Trim()
            : null;

        var body = (primary, author) switch
        {
            (string p, string a) => $"{p}\n\n— {a}",
            (string p, null) => p,
            (null, string a) => $"— {a}",
            _ => null,
        };

        if (body is null)
        {
            return null;
        }

        return body.Length > maxLength
            ? body[..(maxLength - 1)] + "…"
            : body;
    }
}
