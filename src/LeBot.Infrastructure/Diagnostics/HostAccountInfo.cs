using System.Security.Principal;

namespace LeBot.Infrastructure.Diagnostics;

/// <summary>
/// Resolves <see cref="IHostAccountInfo"/> from the live Windows token once at construction. On
/// non-Windows, or if the identity can't be read, it reports <c>false</c> — the warning it gates is
/// Windows-only and failing open keeps a diagnostics fault from ever blocking startup.
/// </summary>
public sealed class HostAccountInfo : IHostAccountInfo
{
    public bool IsLocalSystem { get; }

    public HostAccountInfo() => IsLocalSystem = DetectLocalSystem();

    private static bool DetectLocalSystem()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.IsSystem;
        }
        catch (Exception)
        {
            // Reading the token can fail in locked-down contexts; treat "unknown" as "not LocalSystem"
            // so we never raise a false alarm or, worse, throw out of a constructor on the DI path.
            return false;
        }
    }
}
