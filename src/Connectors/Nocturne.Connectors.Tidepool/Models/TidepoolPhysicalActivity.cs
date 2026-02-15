using System.Text.Json.Serialization;

namespace Nocturne.Connectors.Tidepool.Models;

public class TidepoolPhysicalActivity
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("time")]
    public DateTime? Time { get; set; }

    [JsonPropertyName("uploadId")]
    public string? UploadId { get; set; }

    [JsonPropertyName("distance")]
    public TidepoolMeasurement? Distance { get; set; }

    [JsonPropertyName("duration")]
    public TidepoolMeasurement? Duration { get; set; }

    [JsonPropertyName("energy")]
    public TidepoolMeasurement? Energy { get; set; }
}

public class TidepoolMeasurement
{
    [JsonPropertyName("units")]
    public string Units { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public double Value { get; set; }
}
