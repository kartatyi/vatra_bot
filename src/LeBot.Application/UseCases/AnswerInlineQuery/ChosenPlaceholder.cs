namespace LeBot.Application.UseCases.AnswerInlineQuery;

/// <summary>Someone sent the "loading" placeholder an inline answer offered while their post was still being fetched.</summary>
/// <param name="InlineMessageId">Telegram's handle for the message they sent — what gets edited.</param>
/// <param name="Query">The inline query they typed, which holds the link.</param>
/// <param name="WithText">Whether they picked the variant that carries the post's text.</param>
/// <param name="LanguageCode">Their Telegram interface language, for anything the edit has to say.</param>
public sealed record ChosenPlaceholder(string InlineMessageId, string Query, bool WithText, string? LanguageCode = null);
