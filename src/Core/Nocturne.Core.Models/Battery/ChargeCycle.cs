using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Battery;

/// <summary>
/// Battery charge cycle (from charging to depleted or vice versa).
/// Derived from transitions in <see cref="BatteryReading.IsCharging"/> state.
/// </summary>
/// <seealso cref="BatteryReading"/>
/// <seealso cref="BatteryStatistics"/>
public class ChargeCycle
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("device")]
    public string Device { get; set; } = string.Empty;

    /// <summary>
    /// When charging started (mills)
    /// </summary>
    [JsonPropertyName("chargeStartMills")]
    public long? ChargeStartMills { get; set; }

    /// <summary>
    /// Battery level when charging started
    /// </summary>
    [JsonPropertyName("chargeStartLevel")]
    public int? ChargeStartLevel { get; set; }

    /// <summary>
    /// When charging ended (mills)
    /// </summary>
    [JsonPropertyName("chargeEndMills")]
    public long? ChargeEndMills { get; set; }

    /// <summary>
    /// Battery level when charging ended
    /// </summary>
    [JsonPropertyName("chargeEndLevel")]
    public int? ChargeEndLevel { get; set; }

    /// <summary>
    /// When the discharge started (after unplugging, mills)
    /// </summary>
    [JsonPropertyName("dischargeStartMills")]
    public long? DischargeStartMills { get; set; }

    /// <summary>
    /// Battery level when discharge started
    /// </summary>
    [JsonPropertyName("dischargeStartLevel")]
    public int? DischargeStartLevel { get; set; }

    /// <summary>
    /// When the next charge started (marks end of discharge, mills)
    /// </summary>
    [JsonPropertyName("dischargeEndMills")]
    public long? DischargeEndMills { get; set; }

    [JsonPropertyName("dischargeEndLevel")]
    public int? DischargeEndLevel { get; set; }

    /// <summary>
    /// Charge duration in minutes
    /// </summary>
    [JsonPropertyName("chargeDurationMinutes")]
    public double? ChargeDurationMinutes =>
        ChargeStartMills.HasValue && ChargeEndMills.HasValue
            ? (ChargeEndMills.Value - ChargeStartMills.Value) / 60000.0
            : null;

    /// <summary>
    /// Discharge duration in minutes (time between unplugging and next charge)
    /// </summary>
    [JsonPropertyName("dischargeDurationMinutes")]
    public double? DischargeDurationMinutes =>
        DischargeStartMills.HasValue && DischargeEndMills.HasValue
            ? (DischargeEndMills.Value - DischargeStartMills.Value) / 60000.0
            : null;

    /// <summary>
    /// Whether this cycle is complete (has both charge and discharge data)
    /// </summary>
    [JsonPropertyName("isComplete")]
    public bool IsComplete =>
        ChargeStartMills.HasValue
        && ChargeEndMills.HasValue
        && DischargeStartMills.HasValue
        && DischargeEndMills.HasValue;
}
