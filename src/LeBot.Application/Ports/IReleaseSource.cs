using LeBot.Application.Releases;
using LeBot.Domain.Common;

namespace LeBot.Application.Ports;

/// <summary>
/// The boundary over wherever releases live (today: GitHub Releases). The application core asks
/// for the latest release without knowing the transport or JSON shape behind it.
/// </summary>
public interface IReleaseSource
{
    /// <summary>
    /// Fetches the latest published release. Returns <see cref="Result{TValue, TError}.Err"/> with a
    /// <see cref="ReleaseSourceError"/> when there is nothing to update to, the source is unreachable,
    /// or the response is unusable. Never throws for these expected cases.
    /// </summary>
    Task<Result<ReleaseInfo, ReleaseSourceError>> GetLatestAsync(CancellationToken cancellationToken);
}
