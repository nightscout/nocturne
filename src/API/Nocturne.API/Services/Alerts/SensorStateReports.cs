using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Which <see cref="SensorStateReport"/> states the sensor's state for alert rules
/// (docs/alerts/engine-semantics.md §4, sensor state). The report holds a state the source's
/// allowlist mapped when its status was decomposed (<see cref="Nocturne.API.Services.V4.ReportedSensorStates"/>).
/// </summary>
/// <remarks>
/// A glucose rule whose sensor is not ok holds rather than fires, so a state read wrongly silences a
/// low alarm. That is why a state counts only for the reading it describes (<see cref="Describes"/>),
/// and why it counts only while its report is recent: a source that stops reporting leaves no state
/// behind. Neither source says when its state began.
/// </remarks>
internal static class SensorStateReports
{
    /// <summary>
    /// How long a report counts after it arrived: the engine's default glucose freshness bound, so a
    /// state does not outlive the reading it came with.
    /// </summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(11);

    /// <summary>When a report arrived, as far as it can be told: the earlier of its timestamp and its row's.</summary>
    public static DateTime ArrivedAt(SensorStateReport report) =>
        report.Timestamp < report.CreatedAt ? report.Timestamp : report.CreatedAt;

    /// <summary>
    /// The state the newest report of <paramref name="source"/>'s reading states at
    /// <paramref name="asOf"/>, with the instant it stops counting; null when none does. A report
    /// counts while its timestamp is within the clock tolerance of <paramref name="asOf"/>
    /// (<see cref="AlertReadings.Bound"/>) and it arrived at most <see cref="MaxAge"/> before; reports
    /// are ordered by arrival, so one dated ahead does not outrank a newer one. When the newest holds a
    /// stored state this build has no member for, there is no state: an older report does not speak
    /// for the sensor after it.
    /// </summary>
    public static (SensorState State, DateTime Until)? Newest(
        IEnumerable<SensorStateReport> reports, DateTime asOf, ReadingSource? source) =>
        reports
            .Where(r => r.Timestamp <= AlertReadings.Bound(asOf)
                        && r.CreatedAt <= asOf
                        && asOf - ArrivedAt(r) <= MaxAge
                        && Describes(r, source))
            .MaxBy(ArrivedAt) is { State: { } state } newest
            ? (state, ArrivedAt(newest) + MaxAge)
            : null;

    /// <summary>
    /// Whether <paramref name="report"/> is of the device <paramref name="source"/>'s reading came
    /// from: its device and its data source are both present on both, and equal. CareLink names a
    /// status and its readings <c>CareLink {family}</c> and writes both under its connector's source. A
    /// report or a reading that lacks either says nothing about the other, so it is not one.
    /// </summary>
    public static bool Describes(SensorStateReport report, ReadingSource? source) =>
        source is { Device: { Length: > 0 } device, DataSource: { Length: > 0 } dataSource }
        && string.Equals(report.Device, device, StringComparison.Ordinal)
        && string.Equals(report.DataSource, dataSource, StringComparison.Ordinal);
}
