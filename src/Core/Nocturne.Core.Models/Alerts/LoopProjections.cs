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

/// <summary>Lightweight projection of the active pump-mode StateSpan for alert evaluation.
/// Pump modes are mutually exclusive (one span per category) so this is a single record
/// rather than a collection.</summary>
public sealed record PumpStateSnapshot(PumpModeState Mode, DateTime StartedAt);

/// <summary>Lightweight projection of an active StateSpan for the generic state-span-active
/// condition. The category and state are echoed back so the evaluator can audit lookups,
/// even though the dictionary key already encodes them.</summary>
public sealed record StateSpanSnapshot(StateSpanCategory Category, string? State, DateTime StartedAt);

/// <summary>Lightweight projection of the tenant's currently-active Do Not Disturb state.
/// Populated by the orchestrator when DND is on for any reason (manual toggle with optional
/// auto-expire, or scheduled window). Null when DND is off — including when a manual DND has
/// auto-expired or the scheduled window is not currently in force.</summary>
public sealed record DoNotDisturbSnapshot(DateTime StartedAt, string Source);
