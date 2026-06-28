namespace LeBot.Infrastructure.Diagnostics;

/// <summary>The verdict of a single <c>--doctor</c> self-check.</summary>
public enum DoctorStatus
{
    /// <summary>The check passed — nothing to do.</summary>
    Pass,

    /// <summary>The check is non-fatal but worth the operator's attention (e.g. cookies unreadable).</summary>
    Warn,

    /// <summary>The check failed — the bot will not work correctly until it's fixed.</summary>
    Fail,
}

/// <summary>
/// One line of the <c>--doctor</c> checklist: a named check, its <see cref="DoctorStatus"/>, and a
/// short human-readable detail. Carries no secrets — the detail is safe to print and paste into an
/// issue. Construct via the <see cref="Pass"/> / <see cref="Warn"/> / <see cref="Fail"/> factories.
/// </summary>
public sealed record DoctorCheck(string Name, DoctorStatus Status, string Detail)
{
    public static DoctorCheck Pass(string name, string detail) => new(name, DoctorStatus.Pass, detail);

    public static DoctorCheck Warn(string name, string detail) => new(name, DoctorStatus.Warn, detail);

    public static DoctorCheck Fail(string name, string detail) => new(name, DoctorStatus.Fail, detail);

    /// <summary>A ✓ / ⚠ / ✗ glyph for the console checklist.</summary>
    public string Symbol => Status switch
    {
        DoctorStatus.Pass => "✓",
        DoctorStatus.Warn => "⚠",
        _ => "✗",
    };
}
