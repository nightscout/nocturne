using System.Text.Json.Serialization;

namespace Nocturne.Connectors.Glooko.Models;

/// <summary>
///     Response from /api/v3/session/users endpoint
/// </summary>
public class GlookoV3UsersResponse
{
    [JsonPropertyName("currentUser")] public GlookoV3User? CurrentUser { get; set; }

    [JsonPropertyName("currentPatient")] public GlookoV3User? CurrentPatient { get; set; }
}

/// <summary>
///     Glooko user profile with settings
/// </summary>
public class GlookoV3User
{
    [JsonPropertyName("id")] public string? Id { get; set; }

    [JsonPropertyName("firstName")] public string? FirstName { get; set; }

    [JsonPropertyName("lastName")] public string? LastName { get; set; }

    [JsonPropertyName("email")] public string? Email { get; set; }

    [JsonPropertyName("glookoCode")] public string? GlookoCode { get; set; }

    /// <summary>
    ///     Meter units preference: "mgdl" or "mmol"
    /// </summary>
    [JsonPropertyName("meterUnits")]
    public string MeterUnits { get; set; } = "mgdl";

    /// <summary>
    ///     User's timezone (note: Glooko stores times as "fake UTC" already adjusted)
    /// </summary>
    [JsonPropertyName("timezone")]
    public string? Timezone { get; set; }

    /// <summary>
    ///     Whether user has a closed-loop device connected
    /// </summary>
    [JsonPropertyName("hasClosedLoopDevice")]
    public bool HasClosedLoopDevice { get; set; }

    [JsonPropertyName("preference")] public GlookoV3Preference? Preference { get; set; }

    [JsonPropertyName("lastSyncTimestamps")]
    public GlookoV3LastSyncTimestamps? LastSyncTimestamps { get; set; }
}

/// <summary>
///     User preferences including units and target ranges
/// </summary>
public class GlookoV3Preference
{
    /// <summary>
    ///     Meter units preference: "mgdl" or "mmol"
    /// </summary>
    [JsonPropertyName("meterUnits")]
    public string MeterUnits { get; set; } = "mgdl";

    /// <summary>
    ///     Normal glucose min (internal scaled units)
    /// </summary>
    [JsonPropertyName("normalGlucoseMin")]
    public int? NormalGlucoseMin { get; set; }

    /// <summary>
    ///     Before meal normal glucose max (internal scaled units)
    /// </summary>
    [JsonPropertyName("beforeMealNormalGlucoseMax")]
    public int? BeforeMealNormalGlucoseMax { get; set; }

    /// <summary>
    ///     After meal normal glucose max (internal scaled units)
    /// </summary>
    [JsonPropertyName("afterMealNormalGlucoseMax")]
    public int? AfterMealNormalGlucoseMax { get; set; }

    [JsonPropertyName("language")] public string? Language { get; set; }
}

/// <summary>
///     Last sync timestamps by device type
/// </summary>
public class GlookoV3LastSyncTimestamps
{
    [JsonPropertyName("meter")] public string? Meter { get; set; }

    [JsonPropertyName("pump")] public string? Pump { get; set; }

    [JsonPropertyName("cgmDevice")] public string? CgmDevice { get; set; }

    [JsonPropertyName("insulinPen")] public string? InsulinPen { get; set; }

    [JsonPropertyName("lastSyncTimestamp")]
    public string? LastSyncTimestamp { get; set; }
}