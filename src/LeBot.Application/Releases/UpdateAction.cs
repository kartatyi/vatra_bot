namespace LeBot.Application.Releases;

/// <summary>What the updater should do after comparing the running version to the latest release.</summary>
public enum UpdateAction
{
    /// <summary>Stay on the current version — nothing newer, or updates are disabled.</summary>
    None,

    /// <summary>A newer release exists but the operator only wants a notification.</summary>
    Notify,

    /// <summary>A newer release exists and should be downloaded and installed.</summary>
    Apply,
}
