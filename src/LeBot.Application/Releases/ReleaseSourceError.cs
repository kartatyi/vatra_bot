namespace LeBot.Application.Releases;

/// <summary>Why an <see cref="Ports.IReleaseSource"/> could not produce a <see cref="ReleaseInfo"/>.</summary>
public enum ReleaseSourceError
{
    /// <summary>The repository has no published release yet.</summary>
    NoReleases,

    /// <summary>The release endpoint could not be reached or returned a non-success status.</summary>
    NetworkFailure,

    /// <summary>The release payload was present but could not be parsed.</summary>
    MalformedResponse,

    /// <summary>The release exists but carries no asset with the configured name.</summary>
    AssetMissing,

    /// <summary>The asset exists but no SHA256 digest could be obtained for it.</summary>
    ChecksumMissing,
}
