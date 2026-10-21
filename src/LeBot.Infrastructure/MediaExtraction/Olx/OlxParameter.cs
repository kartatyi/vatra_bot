namespace LeBot.Infrastructure.MediaExtraction.Olx;

/// <summary>One fact from an advert's parameter table, e.g. "Стан: Нове".</summary>
internal sealed record OlxParameter(string Name, string Value);
