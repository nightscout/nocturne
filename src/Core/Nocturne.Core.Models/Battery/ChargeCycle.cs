using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Battery;

/// <summary>
/// Represents a battery charge cycle (from charging to depleted or vice versa)
/// </summary>
public class ChargeCycle
{
    /// <summary>
    /// Gets or sets the unique identifier for this cycle
    /// </summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>
    /// Gets or sets the device identifier/name
    /// </summary>
    [JsonPropertyName("device")]
    public string Device { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when charging started (mills)
    /// </summary>
    [JsonPropertyName("chargeStartMills")]
    public long? ChargeStartMills { get; set; }

    /// <summary>
    /// Gets or sets the battery level when charging started
    /// </summary>
    [JsonPropertyName("chargeStartLevel")]
    public int? ChargeStartLevel { get; set; }

    /// <summary>
    /// Gets or sets when charging ended (mills)
    /// </summary>
    [JsonPropertyName("chargeEndMills")]
    public long? ChargeEndMills { get; set; }

    /// <summary>
    /// Gets or sets the battery level when charging ended
    /// </summary>
    [JsonPropertyName("chargeEndLevel")]
    public int? ChargeEndLevel { get; set; }

    /// <summary>
    /// Gets or sets when the discharge started (after unplugging, mills)
    /// </summary>
    [JsonPropertyName("dischargeStartMills")]
    public long? DischargeStartMills { get; set; }

    /// <summary>
    /// Gets or sets the battery level when discharge started
    /// </summary>
    [JsonPropertyName("dischargeStartLevel")]
    public int? DischargeStartLevel { get; set; }

    /// <summary>
    /// Gets or sets when the next charge started (marks end of discharge, mills)
    /// </summary>
    [JsonPropertyName("dischargeEndMills")]
    public long? DischargeEndMills { get; set; }

    /// <summary>
    /// Gets or sets the battery level at end of discharge
    /// </summary>
    [JsonPropertyName("dischargeEndLevel")]
    public int? DischargeEndLevel { get; set; }

    /// <summary>
    /// Gets the charge duration in minutes
    /// </summary>
    [JsonPropertyName("chargeDurationMinutes")]
    public double? ChargeDurationMinutes =>
        ChargeStartMills.HasValue && ChargeEndMills.HasValue
            ? (ChargeEndMills.Value - ChargeStartMills.Value) / 60000.0
            : null;

    /// <summary>
    /// Gets the discharge duration in minutes (time between unplugging and next charge)
    /// </summary>
    [JsonPropertyName("dischargeDurationMinutes")]
    public double? DischargeDurationMinutes =>
        DischargeStartMills.HasValue && DischargeEndMills.HasValue
            ? (DischargeEndMills.Value - DischargeStartMills.Value) / 60000.0
            : null;

    /// <summary>
    /// Gets whether this cycle is complete (has both charge and discharge data)
    /// </summary>
    [JsonPropertyName("isComplete")]
    public bool IsComplete =>
        ChargeStartMills.HasValue
        && ChargeEndMills.HasValue
        && DischargeStartMills.HasValue
        && DischargeEndMills.HasValue;
}
