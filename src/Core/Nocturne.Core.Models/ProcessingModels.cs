using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// Response model for async processing requests.
/// Returned immediately when the server accepts a bulk import or processing job;
/// the client polls <see cref="StatusUrl"/> to track progress.
/// </summary>
/// <seealso cref="ProcessingStatus"/>
/// <seealso cref="ProcessingStatusResponse"/>
public class AsyncProcessingResponse
{
    [JsonPropertyName("correlationId")]
    public string CorrelationId { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = "accepted";

    [JsonPropertyName("statusUrl")]
    public string StatusUrl { get; set; } = string.Empty;

    [JsonPropertyName("estimatedProcessingTime")]
    public TimeSpan EstimatedProcessingTime { get; set; }

    /// <summary>
    /// Estimated UTC completion time, computed as <c>UtcNow + <see cref="EstimatedProcessingTime"/></c>.
    /// </summary>
    [JsonPropertyName("estimatedCompletion")]
    public DateTime? EstimatedCompletion => DateTime.UtcNow.Add(EstimatedProcessingTime);
}

public class ProcessingStatus
{
    [JsonPropertyName("correlationId")]
    public string CorrelationId { get; set; } = string.Empty;

    /// <summary>
    /// Processing status (pending, processing, completed, failed)
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "pending";

    /// <summary>
    /// Progress percentage (0-100)
    /// </summary>
    [JsonPropertyName("progress")]
    public int Progress { get; set; }

    [JsonPropertyName("processedCount")]
    public int ProcessedCount { get; set; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }

    [JsonPropertyName("startedAt")]
    public DateTime StartedAt { get; set; }

    /// <summary>
    /// When processing completed (null if not completed)
    /// </summary>
    [JsonPropertyName("completedAt")]
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Any errors that occurred during processing
    /// </summary>
    [JsonPropertyName("errors")]
    public List<string> Errors { get; set; } = new();

    /// <summary>
    /// Results of processing (only included when completed)
    /// </summary>
    [JsonPropertyName("results")]
    public object? Results { get; set; }
}

public class ProcessingStatusResponse : ProcessingStatus
{
    // Inherits all properties from ProcessingStatus
    // Can be extended with additional response-specific properties if needed
}
