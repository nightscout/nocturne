using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// Request to accept a compression low suggestion with adjusted bounds.
/// The user may adjust the start and end time detected by the algorithm before accepting.
/// </summary>
/// <seealso cref="CompressionLowSuggestion"/>
public class AcceptSuggestionRequest
{
    /// <summary>Adjusted start of the compression low region (Unix milliseconds)</summary>
    [JsonPropertyName("startMills")]
    public long StartMills { get; set; }

    /// <summary>Adjusted end of the compression low region (Unix milliseconds)</summary>
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

/// <seealso cref="DetectionResult"/>
public class NightDetectionResult
{
    /// <summary>Date string (YYYY-MM-DD) representing the night that was analyzed</summary>
    [JsonPropertyName("nightOf")]
    public string NightOf { get; set; } = string.Empty;

    /// <summary>Number of <see cref="CompressionLowSuggestion"/> records created for this night</summary>
    [JsonPropertyName("suggestionsCreated")]
    public int SuggestionsCreated { get; set; }
}

/// <summary>
/// Result of compression low detection across one or more nights
/// </summary>
/// <seealso cref="NightDetectionResult"/>
/// <seealso cref="TriggerDetectionRequest"/>
public class DetectionResult
{
    /// <summary>Total number of <see cref="CompressionLowSuggestion"/> records created across all processed nights</summary>
    [JsonPropertyName("totalSuggestionsCreated")]
    public int TotalSuggestionsCreated { get; set; }

    [JsonPropertyName("nightsProcessed")]
    public int NightsProcessed { get; set; }

    /// <summary>Per-night breakdown of detection results</summary>
    [JsonPropertyName("results")]
    public List<NightDetectionResult> Results { get; set; } = [];
}
