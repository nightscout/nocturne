namespace Nocturne.Core.Models.V4;

/// <summary>
/// Slim projection of a <see cref="PumpSnapshot"/> carrying only the pump-reported IOB, as
/// <see cref="ApsIobCobPoint"/> does for APS snapshots.
/// </summary>
/// <param name="Timestamp">Snapshot timestamp (UTC).</param>
/// <param name="Iob">Pump-reported total IOB, or <c>null</c> when the snapshot carried none.</param>
/// <param name="BolusIob">Pump-reported bolus IOB, or <c>null</c> when the snapshot carried none.</param>
public readonly record struct PumpIobPoint(DateTime Timestamp, double? Iob, double? BolusIob);
