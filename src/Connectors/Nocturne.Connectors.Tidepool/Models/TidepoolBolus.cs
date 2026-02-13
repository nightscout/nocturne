using System.Text.Json.Serialization;

namespace Nocturne.Connectors.Tidepool.Models;

public class TidepoolBolus
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("normal")]
    public double? Normal { get; set; }

    [JsonPropertyName("extended")]
    public double? Extended { get; set; }

    [JsonPropertyName("duration")]
    public long? DurationMs { get; set; }

    [JsonPropertyName("subType")]
    public string? SubType { get; set; }

    [JsonPropertyName("time")]
    public DateTime? Time { get; set; }

    [JsonPropertyName("uploadId")]
    public string? UploadId { get; set; }

    [JsonIgnore]
    public TimeSpan? Duration => DurationMs.HasValue
        ? TimeSpan.FromMilliseconds(DurationMs.Value)
        : null;
}
