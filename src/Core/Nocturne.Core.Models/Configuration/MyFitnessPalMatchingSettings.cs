using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Configuration;

/// <summary>
/// Global matching settings for MyFitnessPal connector imports.
/// </summary>
public class MyFitnessPalMatchingSettings
{
    [JsonPropertyName("matchTimeWindowMinutes")]
    public int MatchTimeWindowMinutes { get; set; } = 30;

    [JsonPropertyName("matchCarbTolerancePercent")]
    public int MatchCarbTolerancePercent { get; set; } = 20;

    [JsonPropertyName("matchCarbToleranceGrams")]
    public int MatchCarbToleranceGrams { get; set; } = 10;

    [JsonPropertyName("enableMatchNotifications")]
    public bool EnableMatchNotifications { get; set; } = true;
}
