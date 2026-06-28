using LeBot.Domain.Common;

namespace LeBot.Application.Ports;

/// <summary>Exposes the running build's own version so the updater can compare it to releases.</summary>
public interface IAppVersion
{
    /// <summary>
    /// The version of the currently-running binary. A dev or unstamped build reports
    /// <c>0.0.0</c>, which the updater treats as older than any real release.
    /// </summary>
    ReleaseVersion Current { get; }
}
