namespace Nocturne.Core.Models.V4;

/// <summary>
/// Slim projection of an <see cref="ApsSnapshot"/> carrying only the device-reported IOB/COB
/// needed by the chart pipeline. Fetching full snapshots there would round-trip every prediction
/// and decision JSON blob (several KB per row) on each chart-data request.
/// </summary>
/// <param name="Timestamp">Snapshot timestamp (UTC).</param>
/// <param name="Iob">Device-reported total IOB, or <c>null</c> when the snapshot carried none.</param>
/// <param name="Cob">Device-reported COB, or <c>null</c> when the snapshot carried none.</param>
public readonly record struct ApsIobCobPoint(DateTime Timestamp, double? Iob, double? Cob);
