using System.Text.Json.Serialization;

namespace Nocturne.Core.Alerts.Native;

/// <summary>The rule configuration the tracker entry points read.</summary>
public sealed record RustTrackerConfig
{
    [JsonPropertyName("confirmation_readings")]
    public int ConfirmationReadings { get; init; } = 1;

    [JsonPropertyName("hysteresis_minutes")]
    public int HysteresisMinutes { get; init; }
}

/// <summary>Request envelope for <c>nocturne_alerts_tracker_process</c>: one evaluation's truth.</summary>
public sealed record RustTrackerProcessRequest
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = AlertEnvelopeJson.SchemaVersion;

    /// <summary>The rule's persisted tracker state; null when it has never been evaluated.</summary>
    public RustTrackerState? Tracker { get; init; }

    public required DateTime Now { get; init; }

    public required RustTrackerConfig Config { get; init; }

    [JsonPropertyName("condition_met")]
    public required bool ConditionMet { get; init; }
}

/// <summary>Request envelope for <c>nocturne_alerts_tracker_force_close</c>.</summary>
public sealed record RustTrackerForceCloseRequest
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = AlertEnvelopeJson.SchemaVersion;

    public RustTrackerState? Tracker { get; init; }

    public required DateTime Now { get; init; }

    public required RustCloseReason Reason { get; init; }
}

/// <summary>Request envelope for <c>nocturne_alerts_tracker_close_elapsed_hysteresis</c>.</summary>
public sealed record RustTrackerCloseElapsedHysteresisRequest
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = AlertEnvelopeJson.SchemaVersion;

    public RustTrackerState? Tracker { get; init; }

    public required DateTime Now { get; init; }

    public required RustTrackerConfig Config { get; init; }
}

/// <summary>Response envelope shared by the three tracker entry points.</summary>
public sealed record RustTrackerResponse : IRustResponseEnvelope
{
    [JsonPropertyName("schema_version"), JsonRequired]
    public int SchemaVersion { get; init; }

    [JsonRequired]
    public bool Ok { get; init; }

    /// <summary>Error message when <see cref="Ok"/> is false.</summary>
    public string? Error { get; init; }

    public RustTrackerTransition? Transition { get; init; }

    /// <summary>The full post-state, to persist whatever the transition, even <c>none</c>.</summary>
    public RustTrackerState? Tracker { get; init; }
}

/// <summary>The transition a tracker call made.</summary>
public sealed record RustTrackerTransition
{
    [JsonRequired]
    public RustTransition Type { get; init; }

    /// <summary>
    /// The excursion the transition involved. A close has already cleared it from the returned
    /// tracker.
    /// </summary>
    [JsonPropertyName("excursion_ordinal")]
    public int? ExcursionOrdinal { get; init; }

    /// <summary>Present exactly when <see cref="Type"/> is <see cref="RustTransition.Closed"/>.</summary>
    [JsonPropertyName("close_reason")]
    public RustCloseReason? CloseReason { get; init; }
}
