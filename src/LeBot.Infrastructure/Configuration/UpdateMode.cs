namespace LeBot.Infrastructure.Configuration;

/// <summary>How the self-updater reacts to a newer release.</summary>
public enum UpdateMode
{
    /// <summary>Download, verify, and install the new release, then relaunch.</summary>
    Apply,

    /// <summary>Only DM the operator that a newer release exists; never touch the binary.</summary>
    NotifyOnly,
}
