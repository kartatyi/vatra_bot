namespace LeBot.Application.UseCases.AnswerInlineQuery;

/// <summary>The bot-agnostic shape of an inline query: someone typed <c>@bot …</c> in a chat.</summary>
/// <param name="Id">Telegram's query id; the answer must quote it.</param>
/// <param name="Text">What they typed after the bot's name.</param>
public sealed record InlineQuery(string Id, string Text);
