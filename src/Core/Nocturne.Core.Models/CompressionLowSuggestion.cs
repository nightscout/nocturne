using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// Represents a detected compression low suggestion pending user review
/// </summary>
public class CompressionLowSuggestion
{
    /// <summary>
    /// Unique identifier (UUID v7)
    /// </summary>
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    /// <summary>
    /// Start of the detected compression low region (Unix milliseconds)
    /// </summary>
    [JsonPropertyName("startMills")]
    public long StartMills { get; set; }

    /// <summary>
    /// End of the detected compression low region (Unix milliseconds)
    /// </summary>
    [JsonPropertyName("endMills")]
    public long EndMills { get; set; }

    /// <summary>
    /// Confidence score from detection algorithm (0-1)
    /// </summary>
    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    /// <summary>
    /// Current status of the suggestion
    /// </summary>
    [JsonPropertyName("status")]
    public CompressionLowStatus Status { get; set; }

    /// <summary>
    /// The night this compression low was detected for (date of sleep start)
    /// </summary>
    [JsonPropertyName("nightOf")]
    public DateOnly NightOf { get; set; }

    /// <summary>
    /// When this suggestion was created (Unix milliseconds)
    /// </summary>
    [JsonPropertyName("createdAt")]
    public long CreatedAt { get; set; }

    /// <summary>
    /// When this suggestion was reviewed (Unix milliseconds, null if pending)
    /// </summary>
    [JsonPropertyName("reviewedAt")]
    public long? ReviewedAt { get; set; }

    /// <summary>
    /// ID of the created StateSpan (set when accepted)
    /// </summary>
    [JsonPropertyName("stateSpanId")]
    public Guid? StateSpanId { get; set; }

    /// <summary>
    /// Lowest glucose value during the compression low (mg/dL)
    /// </summary>
    [JsonPropertyName("lowestGlucose")]
    public double? LowestGlucose { get; set; }

    /// <summary>
    /// Maximum drop rate observed (mg/dL per minute)
    /// </summary>
    [JsonPropertyName("dropRate")]
    public double? DropRate { get; set; }

    /// <summary>
    /// Time to recover to pre-drop levels (minutes)
    /// </summary>
    [JsonPropertyName("recoveryMinutes")]
    public int? RecoveryMinutes { get; set; }
}

/// <summary>
/// Compression low suggestion with associated glucose entries for charting.
/// Uses composition to wrap a suggestion with additional chart data.
/// </summary>
public class CompressionLowSuggestionWithEntries
{
    /// <summary>
    /// The compression low suggestion
    /// </summary>
    [JsonPropertyName("suggestion")]
    public CompressionLowSuggestion Suggestion { get; set; } = new();

    /// <summary>
    /// Glucose entries for the overnight window
    /// </summary>
    [JsonPropertyName("entries")]
    public IEnumerable<Entry> Entries { get; set; } = [];

    /// <summary>
    /// Treatments for the overnight window (boluses, temp basals, etc.)
    /// </summary>
    [JsonPropertyName("treatments")]
    public IEnumerable<Treatment> Treatments { get; set; } = [];
}
