using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// How a <see cref="SensorStateReport"/> states the sensor's state for alert rules
/// (docs/alerts/engine-semantics.md §4, sensor state). The report holds a state the source's
/// allowlist mapped when its status was decomposed, or none for a value outside it
/// (<see cref="Nocturne.API.Services.V4.ReportedSensorStates"/>). Which report is the newest, and when
/// it arrived, is the repository's (<see cref="ISensorStateReportRepository.GetNewestAsync"/>).
/// </summary>
/// <remarks>
/// A glucose rule whose sensor is not ok holds rather than fires, so a state read wrongly silences a
/// low alarm. That is why a state counts only for the reading it describes, of the same device and
/// data source, and why it counts only while its report is recent: a source that stops reporting
/// leaves no state behind. Neither source says when its state began.
/// </remarks>
internal static class SensorStateReports
{
    /// <summary>
    /// How long a report counts after it arrived: the engine's default glucose freshness bound, so a
    /// state does not outlive the reading it came with.
    /// </summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(11);

    /// <summary>
    /// The state <paramref name="newest"/> states, with the instant it stops counting; null when there
    /// is no report or it holds no state. An older report does not speak for the sensor after it.
    /// </summary>
    public static (SensorState State, DateTime Until)? StateOf(ArrivedSensorStateReport? newest) =>
        newest is { Report.State: { } state } ? (state, newest.ArrivedAt + MaxAge) : null;
}
