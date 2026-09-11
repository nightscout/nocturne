using System.Globalization;

namespace Nocturne.Connectors.Core.Utilities;

/// <summary>
///     When a connector should set its incremental window aside and read the source's whole history
///     again. The last completion is carried as a round-trip timestamp string because the places a
///     connector can keep runtime state — its secrets document, a sync cursor — hold strings.
/// </summary>
public static class FullWalkSchedule
{
    /// <summary>
    ///     Whether a full walk is due: none has ever completed, the stored stamp is unreadable, the
    ///     interval has elapsed, or the stamp lies in the future (a clock moved, and treating that as
    ///     recent would suppress the walk until the clock caught up).
    /// </summary>
    public static bool IsDue(string? lastCompletedAt, TimeSpan interval, DateTimeOffset now)
    {
        if (!DateTimeOffset.TryParse(
                lastCompletedAt,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var last))
            return true;

        return now - last >= interval || last > now;
    }

    /// <summary>The stamp <see cref="IsDue"/> reads back, for a walk that completed at <paramref name="completedAt"/>.</summary>
    public static string Stamp(DateTimeOffset completedAt) =>
        completedAt.ToString("O", CultureInfo.InvariantCulture);
}
