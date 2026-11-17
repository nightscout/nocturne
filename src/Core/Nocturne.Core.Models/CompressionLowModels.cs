using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// What a review applies to every suggestion it names.
/// </summary>
/// <seealso cref="CompressionLowBatchRequest"/>
[JsonConverter(typeof(JsonStringEnumConverter<CompressionLowBatchAction>))]
public enum CompressionLowBatchAction
{
    /// <summary>Accept each suggestion, creating a data exclusion span for it</summary>
    Accept,

    /// <summary>Dismiss each suggestion</summary>
    Dismiss,

    /// <summary>Delete each suggestion and the span an accepted one created</summary>
    Delete,
}

/// <summary>
/// One suggestion in a <see cref="CompressionLowBatchRequest"/>.
/// </summary>
/// <seealso cref="CompressionLowSuggestion"/>
public class CompressionLowBatchItem
{
    /// <summary>The suggestion to act on</summary>
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    /// <summary>
    /// Adjusted start of the compression low region (Unix milliseconds). Read only when accepting;
    /// the detected start is used when it is absent.
    /// </summary>
    [JsonPropertyName("startMills")]
    public long? StartMills { get; set; }

    /// <summary>
    /// Adjusted end of the compression low region (Unix milliseconds). Read only when accepting;
    /// the detected end is used when it is absent.
    /// </summary>
    [JsonPropertyName("endMills")]
    public long? EndMills { get; set; }
}

/// <summary>
/// Request to apply one <see cref="CompressionLowBatchAction"/> to several compression low
/// suggestions.
/// </summary>
/// <seealso cref="CompressionLowBatchItem"/>
public class CompressionLowBatchRequest
{
    /// <summary>The action applied to every item</summary>
    [Required]
    [JsonPropertyName("action")]
    public CompressionLowBatchAction? Action { get; set; }

    /// <summary>The suggestions to act on</summary>
    [JsonPropertyName("items")]
    public List<CompressionLowBatchItem> Items { get; set; } = [];
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
