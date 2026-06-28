using LeBot.Domain.Common;

namespace LeBot.Application.Releases;

/// <summary>
/// A published release the bot could update to. <paramref name="ExpectedSha256"/> is the
/// lowercase 64-hex digest of the asset at <paramref name="AssetUrl"/>; the installer refuses
/// to swap a binary whose computed hash doesn't match it.
/// </summary>
public sealed record ReleaseInfo(
    ReleaseVersion Version,
    Uri AssetUrl,
    string ExpectedSha256,
    string TagName,
    string? ReleaseNotes);
