using LeBot.Domain.Common;

namespace LeBot.Application.Releases;

/// <summary>
/// The pure policy that turns a version comparison into an <see cref="UpdateAction"/>. Kept free
/// of I/O and clocks so the full truth table can be unit-tested without mocks.
/// </summary>
public static class UpdateDecision
{
    /// <summary>
    /// Decides what to do. Disabled or already-current builds yield <see cref="UpdateAction.None"/>;
    /// a strictly newer <paramref name="latest"/> yields <see cref="UpdateAction.Apply"/> when
    /// <paramref name="applyMode"/> is set, otherwise <see cref="UpdateAction.Notify"/>.
    /// </summary>
    public static UpdateAction Evaluate(bool enabled, ReleaseVersion current, ReleaseVersion latest, bool applyMode)
    {
        if (!enabled)
        {
            return UpdateAction.None;
        }

        if (latest <= current)
        {
            return UpdateAction.None;
        }

        return applyMode ? UpdateAction.Apply : UpdateAction.Notify;
    }
}
