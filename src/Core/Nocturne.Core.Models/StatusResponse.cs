using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// Status response model with 1:1 legacy JavaScript compatibility
/// </summary>
public class StatusResponse
{
    /// <summary>
    /// Status message
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "ok";

    /// <summary>
    /// Nightscout name
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    /// Nightscout version
    /// </summary>
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    /// <summary>
    /// API version
    /// </summary>
    [JsonPropertyName("apiVersion")]
    public string? ApiVersion { get; set; }

    /// <summary>
    /// Server time
    /// </summary>
    [JsonPropertyName("serverTime")]
    public DateTime ServerTime { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Server time epoch
    /// </summary>
    [JsonPropertyName("serverTimeEpoch")]
    public long ServerTimeEpoch => ((DateTimeOffset)ServerTime).ToUnixTimeMilliseconds();

    /// <summary>
    /// Enabled features
    /// </summary>
    [JsonPropertyName("enabled")]
    public string[]? Enabled { get; set; }

    /// <summary>
    /// API enabled status
    /// </summary>
    [JsonPropertyName("apiEnabled")]
    public bool ApiEnabled { get; set; } = true;

    /// <summary>
    /// Authorization roles
    /// </summary>
    [JsonPropertyName("roles")]
    public string[]? Roles { get; set; }

    /// <summary>
    /// Settings
    /// </summary>
    [JsonPropertyName("settings")]
    public Dictionary<string, object>? Settings { get; set; }

    /// <summary>
    /// Extended settings
    /// </summary>
    [JsonPropertyName("extendedSettings")]
    public Dictionary<string, object>? ExtendedSettings { get; set; }

    /// <summary>
    /// Careportal enabled status
    /// </summary>
    [JsonPropertyName("careportalEnabled")]
    public bool? CareportalEnabled { get; set; }

    /// <summary>
    /// Bolus calculator enabled status
    /// </summary>
    [JsonPropertyName("boluscalcEnabled")]
    public bool? BoluscalcEnabled { get; set; }

    /// <summary>
    /// Authorized status (null when unauthenticated).
    /// Always serialized for Nightscout compatibility.
    /// </summary>
    [JsonPropertyName("authorized")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public object? Authorized { get; set; }

    /// <summary>
    /// Runtime state of the server
    /// </summary>
    [JsonPropertyName("runtimeState")]
    public string? RuntimeState { get; set; }

    /// <summary>
    /// Server head/git info
    /// </summary>
    [JsonPropertyName("head")]
    public string? Head { get; set; }
}
