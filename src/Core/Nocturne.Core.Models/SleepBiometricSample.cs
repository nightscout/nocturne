using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// A single biometric reading taken during a sleep session.
/// </summary>
public class SleepBiometricSample
{
    /// <summary>
    /// UTC timestamp of the sample.
    /// </summary>
    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Heart rate in beats per minute.
    /// </summary>
    [JsonPropertyName("heartRate")]
    public float? HeartRate { get; set; }

    /// <summary>
    /// Heart rate variability in milliseconds.
    /// </summary>
    [JsonPropertyName("hrv")]
    public float? Hrv { get; set; }

    /// <summary>
    /// Blood oxygen saturation percentage.
    /// </summary>
    [JsonPropertyName("spo2")]
    public float? Spo2 { get; set; }

    /// <summary>
    /// Respiration rate in breaths per minute.
    /// </summary>
    [JsonPropertyName("respirationRate")]
    public float? RespirationRate { get; set; }

    /// <summary>
    /// Movement intensity (device-specific scale).
    /// </summary>
    [JsonPropertyName("movement")]
    public float? Movement { get; set; }
}
