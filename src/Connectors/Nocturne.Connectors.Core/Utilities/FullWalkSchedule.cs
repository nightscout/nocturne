namespace Nocturne.Connectors.Core.Utilities;

/// <summary>
///     When a connector should set its incremental window aside and read the source's whole history
///     again, given when it last did, as kept in its resume state.
/// </summary>
public static class FullWalkSchedule
{
    /// <summary>
    ///     Whether a full walk is due: none has ever completed, the interval has elapsed, or the last
    ///     one lies in the future (a clock moved, and treating that as recent would suppress the walk
    ///     until the clock caught up).
    /// </summary>
    /// <param name="lastCompletedAt">The UTC time the last walk completed, or <c>null</c>.</param>
    public static bool IsDue(DateTime? lastCompletedAt, TimeSpan interval, DateTimeOffset now)
    {
        if (lastCompletedAt is not { } at)
            return true;

        var last = new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc));
        return now - last >= interval || last > now;
    }
}
