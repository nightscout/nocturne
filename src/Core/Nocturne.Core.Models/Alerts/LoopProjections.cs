namespace Nocturne.Core.Models.Alerts;

/// <summary>Lightweight projection of an active temp basal for alert evaluation.
/// Decoupled from V4 ingestion entities so the alert subsystem doesn't depend on Core.Models/V4.</summary>
public sealed record TempBasalSnapshot(
    decimal Rate,
    decimal? ScheduledRate,
    DateTime StartedAt);

/// <summary>Lightweight projection of an active override for alert evaluation.</summary>
public sealed record OverrideSnapshot(
    DateTime StartedAt,
    DateTime? EndsAt,
    decimal? Multiplier,
    string? Name);

/// <summary>Lightweight projection of an active pump-suspension StateSpan for alert evaluation.
/// Set to null when the underlying pump snapshot is stale, so suspension conditions don't latch
/// on stale data after the uploader goes offline.</summary>
public sealed record PumpSuspensionSnapshot(DateTime StartedAt);
