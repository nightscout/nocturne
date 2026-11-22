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
    /// <summary>How far before a reading with no value an alert context looks for one with a value.</summary>
    internal static readonly TimeSpan UsableReadingLookback = TimeSpan.FromHours(24);

    /// <summary>Whether <paramref name="reading"/> carries a glucose value.</summary>
    public static bool HasValue(SensorGlucose reading) => reading.Mgdl > 0;

    /// <summary>
    /// The <see cref="UsableLookBack"/> for <paramref name="newest"/>, a reading with no value,
    /// read at most <see cref="UsableReadingLookback"/> before it.
    /// </summary>
    public static async Task<UsableLookBack> LookBackAsync(
        ICanonicalGlucoseService canonical, SensorGlucose newest, CancellationToken ct)
    {
        var recent = (await canonical.GetRecentAsync(newest.Timestamp - UsableReadingLookback, ct))
            .Where(r => r.Timestamp <= newest.Timestamp)
            .ToList();
        var usable = recent.Where(HasValue).MaxBy(r => r.Timestamp);
        var valueless = recent
            .Where(r => !HasValue(r) && (usable is null || r.Timestamp > usable.Timestamp))
            .Select(r => r.Timestamp)
            .DefaultIfEmpty(newest.Timestamp)
            .Min();
        var at = usable?.Timestamp ?? (recent.Count > 0 ? recent.Min(r => r.Timestamp) : newest.Timestamp);
        return new UsableLookBack(usable, at, valueless);
    }

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

/// <summary>
/// What an alert context reads when its reading has no glucose value: the newest reading before it
/// that has one, which <c>signal_loss</c> and <c>staleness</c> measure from, and the first reading
/// with no value after that, when the sensor's <see cref="Nocturne.Core.Models.Alerts.SensorState.NoValue"/>
/// state began.
/// </summary>
/// <param name="Usable">The newest reading with a value within <see cref="AlertReadings.UsableReadingLookback"/>, or null.</param>
/// <param name="At">
/// What <see cref="Nocturne.Core.Models.SensorContext.LastReadingAt"/> is: <paramref name="Usable"/>'s
/// time, or with none the oldest reading looked at, since the outage is at least that old.
/// </param>
/// <param name="ValuelessSince">The first reading with no value after <paramref name="Usable"/>.</param>
internal sealed record UsableLookBack(SensorGlucose? Usable, DateTime At, DateTime ValuelessSince);

/// <param name="Selected">The reading the alert context is built from, or null when no reading is selectable.</param>
/// <param name="Newest">The newest stored reading by timestamp, which differs from <paramref name="Selected"/> only when it was skipped.</param>
public sealed record AlertReadingSelection(SensorGlucose? Selected, SensorGlucose? Newest);
