using System.Text.Json.Serialization;

namespace Nocturne.Connectors.FreeStyle.Models;

/// <summary>The sensor a LibreLinkUp connection is currently reading from.</summary>
public class LibreSensor
{
    /// <summary>The sensor's serial number.</summary>
    [JsonPropertyName("sn")]
    public string? SerialNumber { get; set; }

    /// <summary>When the sensor was activated, in Unix seconds.</summary>
    [JsonPropertyName("a")]
    public long? ActivatedAt { get; set; }
}
