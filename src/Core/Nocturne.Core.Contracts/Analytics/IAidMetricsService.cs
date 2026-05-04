using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.Analytics;

/// <summary>
/// Service for calculating comprehensive AID (Automated Insulin Delivery) system metrics.
/// Aggregates device segments, APS snapshots, and temp basals into an overall
/// <see cref="AidSystemMetrics"/> assessment.
/// </summary>
/// <seealso cref="IAidDetectionStrategy"/>
/// <seealso cref="AidSystemMetrics"/>
/// <seealso cref="TempBasal"/>
/// <seealso cref="ApsSnapshot"/>
public interface IAidMetricsService
{
    /// <summary>
    /// Calculate comprehensive AID system metrics over a date range.
    /// </summary>
    /// <param name="deviceSegments">Device usage segments with algorithm identification.</param>
    /// <param name="apsSnapshots">APS decision snapshots from the loop system.</param>
    /// <param name="tempBasals"><see cref="TempBasal"/> records issued by the AID system.</param>
    /// <param name="siteChangeCount">Number of infusion site changes in the period.</param>
    /// <param name="cgmDeviceNames">Comma-separated CGM device names, or <c>null</c> if unknown.</param>
    /// <param name="pumpDeviceNames">Comma-separated pump device names, or <c>null</c> if unknown.</param>
    /// <param name="cgmActivePercent">CGM active percentage (0-100), or <c>null</c> if unknown.</param>
    /// <param name="targetLow">Low glucose target in mg/dL, or <c>null</c> if unknown.</param>
    /// <param name="targetHigh">High glucose target in mg/dL, or <c>null</c> if unknown.</param>
    /// <param name="startDate">Start of the analysis period.</param>
    /// <param name="endDate">End of the analysis period.</param>
    /// <returns>Computed <see cref="AidSystemMetrics"/>.</returns>
    AidSystemMetrics Calculate(
        IReadOnlyList<DeviceSegmentInput> deviceSegments,
        IReadOnlyList<ApsSnapshot> apsSnapshots,
        IReadOnlyList<TempBasal> tempBasals,
        int siteChangeCount,
        string? cgmDeviceNames,
        string? pumpDeviceNames,
        double? cgmActivePercent,
        double? targetLow,
        double? targetHigh,
        DateTime startDate,
        DateTime endDate);
}
