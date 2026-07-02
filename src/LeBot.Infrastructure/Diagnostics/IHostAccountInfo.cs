namespace LeBot.Infrastructure.Diagnostics;

/// <summary>
/// Tells the diagnostics layer which Windows account the process runs under. Only the one bit that
/// changes behaviour is exposed — whether we're LocalSystem — so nothing here is PII (no user name).
/// </summary>
public interface IHostAccountInfo
{
    /// <summary>True when the process runs as the LocalSystem (<c>S-1-5-18</c>) service account.</summary>
    bool IsLocalSystem { get; }
}
