using System.Text.Json.Serialization;

namespace Nocturne.Connectors.Tidepool.Models;

public class TidepoolBgValue
{
    [JsonPropertyName("deviceId")]
    public string? DeviceId { get; set; }

    [JsonPropertyName("deviceTime")]
    public DateTime? DeviceTime { get; set; }

    [JsonPropertyName("guid")]
    public string? Guid { get; set; }

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("time")]
    public DateTime? Time { get; set; }

    [JsonPropertyName("units")]
    public string Units { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public double Value { get; set; }

    [JsonPropertyName("uploadId")]
    public string? UploadId { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }
}
