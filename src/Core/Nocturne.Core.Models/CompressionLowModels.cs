using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// Request to accept a compression low suggestion with adjusted bounds
/// </summary>
public class AcceptSuggestionRequest
{
    [JsonPropertyName("startMills")]
    public long StartMills { get; set; }

    [JsonPropertyName("endMills")]
    public long EndMills { get; set; }
}

/// <summary>
/// Request to trigger compression low detection
/// </summary>
public class TriggerDetectionRequest
{
    /// <summary>
    /// Single night to process (use this OR startDate/endDate)
    /// </summary>
    [JsonPropertyName("nightOf")]
    public string? NightOf { get; set; }

    /// <summary>
    /// Start of date range (inclusive)
    /// </summary>
    [JsonPropertyName("startDate")]
    public string? StartDate { get; set; }

    /// <summary>
    /// End of date range (inclusive)
    /// </summary>
    [JsonPropertyName("endDate")]
    public string? EndDate { get; set; }
}

/// <summary>
/// Result of detection for a single night
/// </summary>
public class NightDetectionResult
{
    [JsonPropertyName("nightOf")]
    public string NightOf { get; set; } = string.Empty;

    [JsonPropertyName("suggestionsCreated")]
    public int SuggestionsCreated { get; set; }
}

/// <summary>
/// Result of compression low detection
/// </summary>
public class DetectionResult
{
    [JsonPropertyName("totalSuggestionsCreated")]
    public int TotalSuggestionsCreated { get; set; }

    [JsonPropertyName("nightsProcessed")]
    public int NightsProcessed { get; set; }

    [JsonPropertyName("results")]
    public List<NightDetectionResult> Results { get; set; } = [];
}
