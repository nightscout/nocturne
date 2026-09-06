using System.Text.Json.Serialization;

namespace Nocturne.Connectors.Glooko.Models;

// Slim models for the device-clock probe: the only fields read are the two clocks each record
// carries (fake-UTC clinical timestamp + real-UTC server-side timestamps) and the flags needed to
// drop records that never represent a measurement.

/// <summary>
///     A page of the SSV2 <c>/api/v2/users</c> resource. Unlike <c>/api/v3/session/users</c> it
///     carries the account's observed UTC offset alongside the declared IANA zone — the phone app
///     writes the device's current offset here whenever it drifts, so the two fields are independent
///     signals: a declared zone and an observed clock.
/// </summary>
public class GlookoSsv2UsersPage
{
    [JsonPropertyName("users")] public GlookoSsv2User[]? Users { get; set; }
}

/// <summary>The SSV2 user record (the app's GKUser).</summary>
public class GlookoSsv2User
{
    [JsonPropertyName("guid")] public string? Guid { get; set; }

    /// <summary>
    ///     The session account's own patient code. SSV2 endpoints take no <c>patient=</c> parameter —
    ///     they answer for the session user — so on a linked account (caregiver session, re-linked
    ///     data source) this may differ from the code the windowed fetches query, and clock evidence
    ///     must not be gathered from the wrong person.
    /// </summary>
    [JsonPropertyName("glookoCode")] public string? GlookoCode { get; set; }

    /// <summary>Declared IANA zone; null when the account has never set one.</summary>
    [JsonPropertyName("timezone")] public string? Timezone { get; set; }

    /// <summary>Observed device offset as <c>+HH:MM</c>/<c>-HH:MM</c>, written by the phone app.</summary>
    [JsonPropertyName("utcOffset")] public string? UtcOffset { get; set; }

    /// <summary>Real-UTC time the record last changed — bounds how old <see cref="UtcOffset"/> is.</summary>
    [JsonPropertyName("updatedAt")] public string? UpdatedAt { get; set; }

    /// <summary>
    ///     <c>"server"</c> when no client has ever written the record; such accounts report
    ///     <c>utcOffset: "+00:00"</c> that means "never set", not UTC.
    /// </summary>
    [JsonPropertyName("updatedBy")] public string? UpdatedBy { get; set; }

    [JsonPropertyName("softDeleted")] public bool SoftDeleted { get; set; }
}

/// <summary>A page of <c>/api/v2/cgm/egvs</c> restricted to the clock-bearing fields.</summary>
public class GlookoClockEgvsPage
{
    [JsonPropertyName("egvs")] public GlookoClockEgv[]? Egvs { get; set; }
}

public class GlookoClockEgv
{
    /// <summary>Clinical wall clock (fake UTC).</summary>
    [JsonPropertyName("displayTime")] public string? DisplayTime { get; set; }

    /// <summary>Real-UTC upload time (the app stamps it from an NTP-style server-time probe).</summary>
    [JsonPropertyName("syncTimestamp")] public string? SyncTimestamp { get; set; }

    /// <summary>True for interpolated/back-filled values, which carry no real device clock.</summary>
    [JsonPropertyName("calculated")] public bool Calculated { get; set; }

    [JsonPropertyName("softDeleted")] public bool SoftDeleted { get; set; }
}

/// <summary>
///     A page of <c>/api/v2/pumps/normal_boluses</c> restricted to the clock-bearing fields, plus the
///     SSV2 cursor envelope so the one-time historical evidence scan can paginate.
/// </summary>
public class GlookoClockBolusPage
{
    [JsonPropertyName("normalBoluses")] public GlookoClockBolus[]? NormalBoluses { get; set; }

    [JsonPropertyName("lastPage")] public bool LastPage { get; set; }

    [JsonPropertyName("lastUpdatedAt")] public string? LastUpdatedAt { get; set; }

    [JsonPropertyName("lastGuid")] public string? LastGuid { get; set; }
}

public class GlookoClockBolus
{
    /// <summary>Clinical wall clock (fake UTC).</summary>
    [JsonPropertyName("pumpTimestamp")] public string? PumpTimestamp { get; set; }

    [JsonPropertyName("timestamp")] public string? Timestamp { get; set; }

    /// <summary>Real-UTC upload time.</summary>
    [JsonPropertyName("syncTimestamp")] public string? SyncTimestamp { get; set; }

    [JsonPropertyName("softDeleted")] public bool SoftDeleted { get; set; }
}
