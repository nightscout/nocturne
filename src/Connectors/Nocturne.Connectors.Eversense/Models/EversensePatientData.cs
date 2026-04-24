using System.Text.Json.Serialization;

namespace Nocturne.Connectors.Eversense.Models;

public class EversensePatientDatum
{
    [JsonPropertyName("CurrentGlucose")]
    public double CurrentGlucose { get; set; }

    [JsonPropertyName("GlucoseTrend")]
    public int GlucoseTrend { get; set; }

    [JsonPropertyName("CGTime")]
    public string CgTime { get; set; } = string.Empty;

    [JsonPropertyName("Units")]
    public int Units { get; set; }

    [JsonPropertyName("IsTransmitterConnected")]
    public bool IsTransmitterConnected { get; set; }

    /// <summary>
    /// The patient's username (email). Used for patient selection when following multiple patients.
    /// </summary>
    [JsonPropertyName("userName")]
    public string? UserName { get; set; }

    [JsonPropertyName("firstName")]
    public string? FirstName { get; set; }

    [JsonPropertyName("lastName")]
    public string? LastName { get; set; }
}
