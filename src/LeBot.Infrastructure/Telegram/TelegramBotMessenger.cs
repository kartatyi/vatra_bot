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
/// <see cref="MediaKind"/>, retries transient Telegram failures — <c>429</c>/<c>5xx</c> answers and dropped
/// connections — with exponential backoff, falls back to a plain source-URL reply when the upload still
/// fails, and cleans the local file after the attempt — unless the payload is flagged
/// <see cref="MediaPayload.RetainFiles"/>, which means the media cache owns those bytes.
/// </summary>
public sealed class TelegramBotMessenger(
    ITelegramBotClient bot,
    TimeProvider timeProvider,
    ILogger<TelegramBotMessenger> logger)
    : ITelegramMessenger
{
    // Telegram caps captions on media messages at 1024 chars; standalone text messages at 4096.
    private const int MaxCaptionLength = 1024;
    private const int MaxTextLength = 4096;

    // Telegram media groups must contain 2-10 items.
    private const int MaxAlbumSize = 10;

    private readonly ResiliencePipeline _retryPipeline = new ResiliencePipelineBuilder { TimeProvider = timeProvider }
        .AddRetry(new RetryStrategyOptions
        {
            ShouldHandle = new PredicateBuilder()
                .Handle<ApiRequestException>(IsTransientTelegramError)
                .Handle<RequestException>(IsDroppedConnection),
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
                    args.Outcome.Exception is { } ex ? Describe(ex) : null);
                return ValueTask.CompletedTask;
            },
        })
        .Build();

    public async Task<DeliveryError?> ReplyWithMediaAsync(
        long chatId,
        int replyToMessageId,
        MediaPayload payload,
        CancellationToken cancellationToken)
    {
        if (payload.Items.Count == 0)
        {
            return null;
        }

        var caption = BuildBody(payload, MaxCaptionLength);
        var reply = new ReplyParameters { MessageId = replyToMessageId };

        return payload.Items.Count == 1
            ? await SendSingleAsync(chatId, reply, payload.Items[0], caption, payload, cancellationToken)
            : await SendAlbumAsync(chatId, reply, payload.Items, caption, payload, cancellationToken);
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

    private async Task<DeliveryError?> SendSingleAsync(
        long chatId,
        ReplyParameters reply,
        MediaItem item,
        string? caption,
        MediaPayload payload,
        CancellationToken cancellationToken)
    {
        try
        {
            await SendWithRetryAsync(ct => SendItemAsync(chatId, reply, item, caption, ct), cancellationToken);
            return null;
        }
        catch (RequestException ex)
        {
            return await FallBackToSourceLinkAsync(chatId, reply, "media", 1, payload, ex, cancellationToken);
        }
        finally
        {
            DeleteUnlessRetained(payload, item.FilePath);
        }
    }

    private async Task<DeliveryError?> SendAlbumAsync(
        long chatId,
        ReplyParameters reply,
        IReadOnlyList<MediaItem> items,
        string? caption,
        MediaPayload payload,
        CancellationToken cancellationToken)
    {
        var batch = items.Count > MaxAlbumSize ? items.Take(MaxAlbumSize).ToList() : items;

        try
        {
            await SendWithRetryAsync(ct => SendMediaGroupAsync(chatId, reply, batch, caption, ct), cancellationToken);
            return null;
        }
        catch (RequestException ex)
        {
            return await FallBackToSourceLinkAsync(chatId, reply, "album", batch.Count, payload, ex, cancellationToken);
        }
        finally
        {
            foreach (var item in items)
            {
                DeleteUnlessRetained(payload, item.FilePath);
            }
        }
    }

    /// <summary>
    /// One attempt at a single item. The file is opened here, per attempt, never ahead of the retry:
    /// Telegram.Bot disposes the upload stream when its request ends — failed or not — so a retry handed
    /// the previous attempt's stream would only fail again on a closed file.
    /// </summary>
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
                // supportsStreaming makes Telegram prepare the upload for progressive playback
                // (moov atom up front) and render the inline player with a play button — without
                // it a non-faststart MP4 falls back to a generic file with a download button.
                // duration is the one dimension we have; width/height Telegram reads from the bytes.
                await bot.SendVideo(
                    chatId: chatId,
                    video: inputFile,
                    duration: item.DurationSeconds,
                    caption: caption,
                    supportsStreaming: true,
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

    /// <summary>One attempt at an album; opens its own streams for the same reason as <see cref="SendItemAsync"/>.</summary>
    private async Task SendMediaGroupAsync(
        long chatId,
        ReplyParameters reply,
        IReadOnlyList<MediaItem> batch,
        string? caption,
        CancellationToken cancellationToken)
    {
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
                    MediaKind.Video => new InputMediaVideo(inputFile) { Caption = itemCaption, SupportsStreaming = true },
                    _ => new InputMediaDocument(inputFile) { Caption = itemCaption },
                };
                album.Add(media);
            }

            await bot.SendMediaGroup(
                chatId: chatId,
                media: album,
                replyParameters: reply,
                cancellationToken: cancellationToken);
        }
        finally
        {
            foreach (var stream in streams)
            {
                await stream.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Names why an upload failed and replies with the source link in its place, so the chat still gets
    /// something to tap. The error is returned even when that reply fails too: the caller journals it
    /// either way, and the journal row is what puts the failure in front of the watchdog.
    /// </summary>
    private async Task<DeliveryError> FallBackToSourceLinkAsync(
        long chatId,
        ReplyParameters reply,
        string noun,
        int itemCount,
        MediaPayload payload,
        RequestException failure,
        CancellationToken cancellationToken)
    {
        DeliveryError error = failure is ApiRequestException refusal
            ? new DeliveryError.TelegramRefused(refusal.ErrorCode, refusal.Message)
            : new DeliveryError.TelegramUnreachable(Describe(failure));

        logger.LogWarning(
            failure,
            "Could not deliver {Count} media item(s): {Reason}. Falling back to source link.",
            itemCount, error.Reason);

        try
        {
            await SendWithRetryAsync(ct => bot.SendMessage(
                chatId: chatId,
                text: $"Couldn't repost the {noun}. Source: {payload.SourceUrl}",
                replyParameters: reply,
                cancellationToken: ct), cancellationToken);
        }
        catch (RequestException ex)
        {
            logger.LogError(ex, "Source-link fallback failed as well; the chat got no reply: {Reason}", Describe(ex));
        }

        return error;
    }

    private ValueTask SendWithRetryAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken) =>
        _retryPipeline.ExecuteAsync(async token => await action(token), cancellationToken);

    private static bool IsTransientTelegramError(ApiRequestException ex) =>
        ex.ErrorCode == 429 || ex.ErrorCode is >= 500 and < 600;

    /// <summary>
    /// The connection dropped before Telegram answered — a reset mid-upload, DNS, TLS. Timeouts are left
    /// out on purpose: one has already spent HttpClient's whole budget on this upload, and paying it three
    /// more times keeps the chat waiting minutes for the same fallback. A reset can in principle land after
    /// Telegram already posted the message; a rare duplicate is the price of not going silent.
    /// </summary>
    private static bool IsDroppedConnection(RequestException ex) =>
        ex is not ApiRequestException && ex.InnerException is HttpRequestException;

    /// <summary>
    /// Telegram.Bot wraps every transport failure in the same "Exception during making request", so the
    /// cause worth reading sits further down the chain.
    /// </summary>
    private static string Describe(Exception ex)
    {
        if (ex is ApiRequestException || ex.InnerException is not { } cause)
        {
            return ex.Message;
        }

        var root = cause.GetBaseException();
        return ReferenceEquals(root, cause) ? cause.Message : $"{cause.Message} ({root.Message})";
    }

    /// <summary>
    /// Deletes a just-sent file unless the payload says otherwise. Cached payloads point straight at
    /// the media cache's own copies — deleting those would throw away exactly what the cache exists
    /// to keep, and the next repost of the link would go back to the platform for nothing.
    /// </summary>
    private void DeleteUnlessRetained(MediaPayload payload, string filePath)
    {
        if (payload.RetainFiles)
        {
            return;
        }

        BestEffortDelete(filePath);
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
