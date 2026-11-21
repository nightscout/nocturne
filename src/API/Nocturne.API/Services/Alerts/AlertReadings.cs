using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// The reading an alert context is built from (docs/alerts/engine-semantics.md §4): the newest
/// canonical reading dated no later than now plus the engine's clock tolerance
/// (<see cref="RustAlertEngine.ClockTolerance"/>). A reading dated further ahead is skipped until
/// its time comes within the tolerance, so the readings that arrive meanwhile keep driving the
/// rules. Stored data and the canonical stream charts and statistics read are untouched.
/// </summary>
internal static class AlertReadings
{
    /// <summary>The latest timestamp a reading an alert context reads at <paramref name="now"/> may have.</summary>
    public static DateTime Bound(DateTime now) => now + RustAlertEngine.ClockTolerance();

    /// <summary>
    /// The selected reading and the newest stored one. One read when the newest is within the
    /// tolerance; a second, bounded one only when it is not.
    /// </summary>
    public static async Task<AlertReadingSelection> SelectAsync(
        ICanonicalGlucoseService canonical, DateTime now, CancellationToken ct)
    {
        var newest = await canonical.GetLatestAsync(ct);
        var bound = Bound(now);
        return newest is null || newest.Timestamp <= bound
            ? new AlertReadingSelection(newest, newest)
            : new AlertReadingSelection(await canonical.GetLatestAsync(bound, ct), newest);
    }
}

/// <param name="Selected">The reading the alert context is built from, or null when no reading is selectable.</param>
/// <param name="Newest">The newest stored reading by timestamp, which differs from <paramref name="Selected"/> only when it was skipped.</param>
public sealed record AlertReadingSelection(SensorGlucose? Selected, SensorGlucose? Newest);
