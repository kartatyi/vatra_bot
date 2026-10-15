using LeBot.Application.UseCases.AnswerInlineQuery;

namespace LeBot.Application.Ports;

/// <summary>Sends the answer to an inline query.</summary>
public interface IInlineAnswerer
{
    /// <summary>Answers query <paramref name="queryId"/> with <paramref name="answer"/>, in the asker's <paramref name="languageCode"/>.</summary>
    Task AnswerAsync(string queryId, InlineAnswer answer, string? languageCode, CancellationToken cancellationToken);
}
