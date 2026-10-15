using LeBot.Application.UseCases.AnswerInlineQuery;

namespace LeBot.Application.Ports;

/// <summary>Sends the answer to an inline query.</summary>
public interface IInlineAnswerer
{
    /// <summary>Answers query <paramref name="queryId"/> with <paramref name="answer"/>.</summary>
    Task AnswerAsync(string queryId, InlineAnswer answer, CancellationToken cancellationToken);
}
