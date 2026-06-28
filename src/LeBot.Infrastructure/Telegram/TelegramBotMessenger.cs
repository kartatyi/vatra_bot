using LeBot.Application.Ports;
using LeBot.Domain.Media;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace LeBot.Infrastructure.Telegram;

/// <summary>
/// Adapter over <see cref="ITelegramBotClient"/>. Picks the right send-method for each
/// <see cref="MediaKind"/>, retries transient Telegram failures with exponential backoff,
/// falls back to a plain source-URL reply when the upload is permanently refused, and
/// always cleans the local file after the attempt.
/// </summary>
public sealed class TelegramBotMessenger(
    ITelegramBotClient bot,
    ILogger<TelegramBotMessenger> logger)
    : ITelegramMessenger
{
    // Telegram caps captions on media messages at 1024 chars; standalone text messages at 4096.
    private const int MaxCaptionLength = 1024;
    private const int MaxTextLength = 4096;

    // Telegram media groups must contain 2-10 items.
    private const int MaxAlbumSize = 10;

    private readonly ResiliencePipeline _retryPipeline = new ResiliencePipelineBuilder()
        .AddRetry(new RetryStrategyOptions
        {
            ShouldHandle = new PredicateBuilder().Handle<ApiRequestException>(IsTransientTelegramError),
            MaxRetryAttempts = 3,
            BackoffType = DelayBackoffType.Exponential,
            Delay = TimeSpan.FromMilliseconds(500),
            MaxDelay = TimeSpan.FromSeconds(15),
            UseJitter = true,
            OnRetry = args =>
            {
                logger.LogWarning(
                    "Retrying Telegram call: attempt {Attempt} after {DelayMs}ms ({Reason})",
                    args.AttemptNumber + 1,
                    args.RetryDelay.TotalMilliseconds,
                    args.Outcome.Exception?.Message);
                return ValueTask.CompletedTask;
            },
        })
        .Build();

    public async Task ReplyWithMediaAsync(
        long chatId,
        int replyToMessageId,
        MediaPayload payload,
        CancellationToken cancellationToken)
    {
        if (payload.Items.Count == 0)
        {
            return;
        }

        var caption = BuildBody(payload, MaxCaptionLength);
        var reply = new ReplyParameters { MessageId = replyToMessageId };

        if (payload.Items.Count == 1)
        {
            await SendSingleAsync(chatId, reply, payload.Items[0], caption, payload.SourceUrl, cancellationToken);
        }
        else
        {
            await SendAlbumAsync(chatId, reply, payload.Items, caption, payload.SourceUrl, cancellationToken);
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

        await SendWithRetryAsync(ct => bot.SendMessage(
            chatId: chatId,
            text: body,
            replyParameters: new ReplyParameters { MessageId = replyToMessageId },
            cancellationToken: ct), cancellationToken);
    }

    public async Task SendTextAsync(long chatId, string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var body = text.Length > MaxTextLength ? text[..(MaxTextLength - 1)] + "…" : text;

        await SendWithRetryAsync(ct => bot.SendMessage(
            chatId: chatId,
            text: body,
            cancellationToken: ct), cancellationToken);
    }

    public IAsyncDisposable IndicateBusy(long chatId, BusyKind kind)
    {
        var action = kind switch
        {
            BusyKind.UploadingPhoto => ChatAction.UploadPhoto,
            BusyKind.Typing => ChatAction.Typing,
            _ => ChatAction.UploadVideo,
        };
        return new TelegramBusyIndicator(bot, chatId, action, logger);
    }

    private async Task SendSingleAsync(
        long chatId,
        ReplyParameters reply,
        MediaItem item,
        string? caption,
        Uri sourceUrl,
        CancellationToken cancellationToken)
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
            await SendWithRetryAsync(ct => bot.SendMessage(
                chatId: chatId,
                text: $"Couldn't repost the media. Source: {sourceUrl}",
                replyParameters: reply,
                cancellationToken: ct), cancellationToken);
        }
        finally
        {
            BestEffortDelete(item.FilePath);
        }
    }

    private async Task SendAlbumAsync(
        long chatId,
        ReplyParameters reply,
        IReadOnlyList<MediaItem> items,
        string? caption,
        Uri sourceUrl,
        CancellationToken cancellationToken)
    {
        var batch = items.Count > MaxAlbumSize ? items.Take(MaxAlbumSize).ToList() : items;
        var streams = new List<FileStream>(batch.Count);

        try
        {
            var album = new List<IAlbumInputMedia>(batch.Count);
            for (var i = 0; i < batch.Count; i++)
            {
                var item = batch[i];
                var stream = File.OpenRead(item.FilePath);
                streams.Add(stream);
                var inputFile = InputFile.FromStream(stream, Path.GetFileName(item.FilePath));
                var itemCaption = i == 0 ? caption : null;

                IAlbumInputMedia media = item.Kind switch
                {
                    MediaKind.Photo => new InputMediaPhoto(inputFile) { Caption = itemCaption },
                    MediaKind.Video => new InputMediaVideo(inputFile) { Caption = itemCaption },
                    _ => new InputMediaDocument(inputFile) { Caption = itemCaption },
                };
                album.Add(media);
            }

            try
            {
                await SendWithRetryAsync(ct => bot.SendMediaGroup(
                    chatId: chatId,
                    media: album,
                    replyParameters: reply,
                    cancellationToken: ct), cancellationToken);
            }
            catch (ApiRequestException ex)
            {
                logger.LogWarning(
                    ex,
                    "Telegram refused album of {Count} items (code {ErrorCode}): {Message}. Falling back to source link.",
                    batch.Count, ex.ErrorCode, ex.Message);
                await SendWithRetryAsync(ct => bot.SendMessage(
                    chatId: chatId,
                    text: $"Couldn't repost the album. Source: {sourceUrl}",
                    replyParameters: reply,
                    cancellationToken: ct), cancellationToken);
            }
        }
        finally
        {
            foreach (var stream in streams)
            {
                await stream.DisposeAsync();
            }
            foreach (var item in items)
            {
                BestEffortDelete(item.FilePath);
            }
        }
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
                await SendWithRetryAsync(ct => bot.SendVideo(
                    chatId: chatId,
                    video: inputFile,
                    caption: caption,
                    replyParameters: reply,
                    cancellationToken: ct), cancellationToken);
                break;
            case MediaKind.Photo:
                await SendWithRetryAsync(ct => bot.SendPhoto(
                    chatId: chatId,
                    photo: inputFile,
                    caption: caption,
                    replyParameters: reply,
                    cancellationToken: ct), cancellationToken);
                break;
            case MediaKind.Animation:
                await SendWithRetryAsync(ct => bot.SendAnimation(
                    chatId: chatId,
                    animation: inputFile,
                    caption: caption,
                    replyParameters: reply,
                    cancellationToken: ct), cancellationToken);
                break;
            case MediaKind.Audio:
                await SendWithRetryAsync(ct => bot.SendAudio(
                    chatId: chatId,
                    audio: inputFile,
                    caption: caption,
                    replyParameters: reply,
                    cancellationToken: ct), cancellationToken);
                break;
        }
    }

    private ValueTask SendWithRetryAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken) =>
        _retryPipeline.ExecuteAsync(async token => await action(token), cancellationToken);

    private static bool IsTransientTelegramError(ApiRequestException ex) =>
        ex.ErrorCode == 429 || ex.ErrorCode is >= 500 and < 600;

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
