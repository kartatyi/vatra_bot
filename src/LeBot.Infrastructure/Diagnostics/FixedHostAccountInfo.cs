namespace LeBot.Infrastructure.Diagnostics;

/// <summary>
/// An <see cref="IHostAccountInfo"/> with a fixed answer, for callers that know the target account out
/// of band rather than from the live token — e.g. the installer, which registers the bot to run as
/// LocalSystem no matter which elevated user invokes <c>--install</c>.
/// </summary>
public sealed class FixedHostAccountInfo(bool isLocalSystem) : IHostAccountInfo
{
    public bool IsLocalSystem { get; } = isLocalSystem;
}
