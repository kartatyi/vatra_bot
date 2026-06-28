namespace LeBot.Infrastructure.Maintenance;

/// <summary>Why <see cref="UpdateInstaller.DownloadAndVerifyAsync"/> could not stage a verified binary.</summary>
internal enum UpdateInstallError
{
    /// <summary>The asset could not be downloaded.</summary>
    DownloadFailed,

    /// <summary>The downloaded asset's SHA256 did not match the expected digest.</summary>
    ShaMismatch,

    /// <summary>The staged file could not be written to the install directory.</summary>
    WriteFailed,
}
