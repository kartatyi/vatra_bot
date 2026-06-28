namespace LeBot.Domain.Common;

/// <summary>Why a <see cref="ReleaseVersion.Parse"/> call rejected its input.</summary>
public enum VersionParseError
{
    /// <summary>The input was null, empty, or whitespace.</summary>
    Empty,

    /// <summary>The input was not three dot-separated integers after stripping the optional prefix/suffix.</summary>
    Malformed,
}
