using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

public class OpenApsMmTune
{
    [JsonPropertyName("scanDetails")]
    public List<List<object>>? ScanDetails { get; set; }

    [JsonPropertyName("setFreq")]
    public double? SetFreq { get; set; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    /// <summary>
    /// Whether default was used
    /// </summary>
    [JsonPropertyName("usedDefault")]
    public bool? UsedDefault { get; set; }

    public DateTime? Moment { get; set; }
}
