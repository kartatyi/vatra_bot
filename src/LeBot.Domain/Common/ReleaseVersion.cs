namespace LeBot.Domain.Common;

/// <summary>
/// A semantic-version triple (<c>Major.Minor.Patch</c>) — the bot's single source of version
/// truth, derived from the git release tag. Prerelease (<c>-rc.1</c>) and build (<c>+abc</c>)
/// suffixes are recognised but ignored: ordering is by the three numeric segments only.
/// </summary>
public sealed record ReleaseVersion(int Major, int Minor, int Patch) : IComparable<ReleaseVersion>
{
    /// <summary>
    /// Parses a version string such as <c>v1.2.3</c>, <c>1.2.3</c>, <c>1.2.3-rc.1</c>, or
    /// <c>1.2.3+build.7</c>. Leading <c>v</c>/<c>V</c> and any prerelease/build suffix are stripped
    /// before the three numeric segments are read. Never throws — failures come back as
    /// <see cref="VersionParseError"/>.
    /// </summary>
    public static Result<ReleaseVersion, VersionParseError> Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Result<ReleaseVersion, VersionParseError>.Failure(VersionParseError.Empty);
        }

        var trimmed = raw.Trim();
        if (trimmed.Length > 0 && (trimmed[0] is 'v' or 'V'))
        {
            trimmed = trimmed[1..];
        }

        var buildSplit = trimmed.IndexOf('+', StringComparison.Ordinal);
        if (buildSplit >= 0)
        {
            trimmed = trimmed[..buildSplit];
        }

        var prereleaseSplit = trimmed.IndexOf('-', StringComparison.Ordinal);
        if (prereleaseSplit >= 0)
        {
            trimmed = trimmed[..prereleaseSplit];
        }

        var segments = trimmed.Split('.');
        if (segments.Length != 3)
        {
            return Result<ReleaseVersion, VersionParseError>.Failure(VersionParseError.Malformed);
        }

        if (!int.TryParse(segments[0], out var major)
            || !int.TryParse(segments[1], out var minor)
            || !int.TryParse(segments[2], out var patch))
        {
            return Result<ReleaseVersion, VersionParseError>.Failure(VersionParseError.Malformed);
        }

        return Result<ReleaseVersion, VersionParseError>.Success(new ReleaseVersion(major, minor, patch));
    }

    /// <summary>Orders by <see cref="Major"/>, then <see cref="Minor"/>, then <see cref="Patch"/>.</summary>
    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var major = Major.CompareTo(other.Major);
        if (major != 0)
        {
            return major;
        }

        var minor = Minor.CompareTo(other.Minor);
        return minor != 0 ? minor : Patch.CompareTo(other.Patch);
    }

    public static bool operator >(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) > 0;

    public static bool operator <(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) < 0;

    public static bool operator >=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) >= 0;

    public static bool operator <=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) <= 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}
