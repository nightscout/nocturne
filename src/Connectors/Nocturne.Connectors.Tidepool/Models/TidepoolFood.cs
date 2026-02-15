using System.Text.Json.Serialization;

namespace Nocturne.Connectors.Tidepool.Models;

public class TidepoolFood
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("nutrition")]
    public TidepoolNutrition? Nutrition { get; set; }

    [JsonPropertyName("time")]
    public DateTime? Time { get; set; }

    [JsonPropertyName("uploadId")]
    public string? UploadId { get; set; }
}

public class TidepoolNutrition
{
    [JsonPropertyName("carbohydrate")]
    public TidepoolCarbohydrate? Carbohydrate { get; set; }
}

public class TidepoolCarbohydrate
{
    [JsonPropertyName("net")]
    public double? Net { get; set; }

    [JsonPropertyName("units")]
    public string? Units { get; set; }
}
