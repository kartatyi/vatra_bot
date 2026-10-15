using LeBot.Application.Ports;
using LeBot.Domain.Media;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace LeBot.Infrastructure.Telegram;

/// <summary>
/// Adapter over <see cref="ITelegramBotClient"/>. Picks the right send-method for each
/// <see cref="MediaKind"/>, retries transient Telegram failures with exponential backoff,
/// falls back to a plain source-URL reply when the upload is permanently refused, and cleans
/// the local file after the attempt — unless the payload is flagged
/// <see cref="MediaPayload.RetainFiles"/>, which means the media cache owns those bytes.
/// </summary>
public sealed class TelegramBotMessenger(
    ITelegramBotClient bot,
    ILogger<TelegramBotMessenger> logger)
    : ITelegramMessenger
{
    // Telegram caps captions on media messages at 1024 chars; standalone text messages at 4096.
    internal const int MaxCaptionLength = 1024;
    internal const int MaxTextLength = 4096;

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

    public async Task<int?> ReplyWithMediaAsync(
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

        if (payload.Items.Count == 1)
        {
            return await SendSingleAsync(chatId, reply, payload.Items[0], caption, payload, cancellationToken);
        }

        // sendMediaGroup takes no reply_markup, so an album can't carry the reveal button.
        await SendAlbumAsync(chatId, reply, payload.Items, caption, payload, cancellationToken);
        return null;
    }

    public async Task<int?> ReplyWithTextAsync(
        long chatId,
        int replyToMessageId,
        MediaPayload payload,
        CancellationToken cancellationToken)
    {
        var body = BuildBody(payload, MaxTextLength);
        if (string.IsNullOrEmpty(body))
        {
            return null;
        }

        var sent = await SendWithRetryAsync(ct => bot.SendMessage(
            chatId: chatId,
            text: body,
            replyParameters: new ReplyParameters { MessageId = replyToMessageId },
            replyMarkup: RevealButton.For(payload),
            cancellationToken: ct), cancellationToken);
        return sent.MessageId;
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

    public async Task SetReactionAsync(long chatId, int messageId, LinkReaction? reaction, CancellationToken cancellationToken)
    {
        // An empty list is how the Bot API takes the bot's reaction off.
        ReactionType[] reactions = reaction is { } r ? [new ReactionTypeEmoji { Emoji = EmojiFor(r) }] : [];
        try
        {
            await bot.SetMessageReaction(chatId, messageId, reactions, cancellationToken: cancellationToken);
        }
        catch (RequestException ex)
        {
            // Cosmetic: a chat can restrict which reactions are allowed, and that must never cost the repost.
            logger.LogDebug(ex, "Could not set reaction {Reaction} in chat {ChatId}", reaction, chatId);
        }
    }

    /// <summary>The emoji behind each reaction — all from the set Telegram lets bots react with.</summary>
    internal static string EmojiFor(LinkReaction reaction) => reaction switch
    {
        LinkReaction.Working => "👀",
        LinkReaction.Repeat => "🥱",
        _ => throw new ArgumentOutOfRangeException(nameof(reaction), reaction, "Unknown reaction."),
    };

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

    private async Task<int?> SendSingleAsync(
        long chatId,
        ReplyParameters reply,
        MediaItem item,
        string? caption,
        MediaPayload payload,
        CancellationToken cancellationToken)
    {
        try
        {
            var sent = await SendItemAsync(chatId, reply, item, caption, RevealButton.For(payload), cancellationToken);
            return sent.MessageId;
        }
        catch (ApiRequestException ex)
        {
            logger.LogWarning(
                ex,
                "Telegram refused media (code {ErrorCode}): {Message}. Falling back to source link.",
                ex.ErrorCode, ex.Message);
            await SendWithRetryAsync(ct => bot.SendMessage(
                chatId: chatId,
                text: $"Couldn't repost the media. Source: {payload.SourceUrl}",
                replyParameters: reply,
                cancellationToken: ct), cancellationToken);
            return null;
        }
        finally
        {
            DeleteUnlessRetained(payload, item.FilePath);
        }
    }

    private async Task SendAlbumAsync(
        long chatId,
        ReplyParameters reply,
        IReadOnlyList<MediaItem> items,
        string? caption,
        MediaPayload payload,
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
                    MediaKind.Video => new InputMediaVideo(inputFile) { Caption = itemCaption, SupportsStreaming = true },
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
                    text: $"Couldn't repost the album. Source: {payload.SourceUrl}",
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
                DeleteUnlessRetained(payload, item.FilePath);
            }
        }
    }

    private async Task<Message> SendItemAsync(
        long chatId,
        ReplyParameters reply,
        MediaItem item,
        string? caption,
        InlineKeyboardMarkup? keyboard,
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
                return await SendWithRetryAsync(ct => bot.SendVideo(
                    chatId: chatId,
                    video: inputFile,
                    duration: item.DurationSeconds,
                    caption: caption,
                    supportsStreaming: true,
                    replyParameters: reply,
                    replyMarkup: keyboard,
                    cancellationToken: ct), cancellationToken);

            case MediaKind.Photo:
                return await SendWithRetryAsync(ct => bot.SendPhoto(
                    chatId: chatId,
                    photo: inputFile,
                    caption: caption,
                    replyParameters: reply,
                    replyMarkup: keyboard,
                    cancellationToken: ct), cancellationToken);
            case MediaKind.Animation:
                return await SendWithRetryAsync(ct => bot.SendAnimation(
                    chatId: chatId,
                    animation: inputFile,
                    caption: caption,
                    replyParameters: reply,
                    replyMarkup: keyboard,
                    cancellationToken: ct), cancellationToken);
            case MediaKind.Audio:
                return await SendWithRetryAsync(ct => bot.SendAudio(
                    chatId: chatId,
                    audio: inputFile,
                    caption: caption,
                    replyParameters: reply,
                    replyMarkup: keyboard,
                    cancellationToken: ct), cancellationToken);
            default:
                throw new ArgumentOutOfRangeException(nameof(item), item.Kind, "Unknown media kind.");
        }
    }

    private ValueTask SendWithRetryAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken) =>
        _retryPipeline.ExecuteAsync(async token => await action(token), cancellationToken);

    private ValueTask<T> SendWithRetryAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken) =>
        _retryPipeline.ExecuteAsync(async token => await action(token), cancellationToken);

    private static bool IsTransientTelegramError(ApiRequestException ex) =>
        ex.ErrorCode == 429 || ex.ErrorCode is >= 500 and < 600;

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

    private static string? BuildBody(MediaPayload payload, int maxLength) => Fit(payload.Body, maxLength);

    /// <summary>Cuts <paramref name="text"/> to Telegram's <paramref name="maxLength"/>, marking the cut with an ellipsis.</summary>
    internal static string? Fit(string? text, int maxLength)
    {
        if (text is null)
        {
            return null;
        }

        return text.Length > maxLength
            ? text[..(maxLength - 1)] + "…"
            : text;
    }
}
