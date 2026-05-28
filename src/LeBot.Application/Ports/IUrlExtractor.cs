namespace LeBot.Application.Ports;

/// <summary>
/// Pulls absolute <c>http(s)</c> URLs out of a piece of free-form text.
/// </summary>
public interface IUrlExtractor
{
    /// <summary>Returns every absolute URL found in <paramref name="text"/>, in order of appearance, deduplicated.</summary>
    IReadOnlyList<Uri> Extract(string text);
}
