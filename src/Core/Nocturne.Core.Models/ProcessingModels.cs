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
    /// <summary>
    /// Gets or sets the correlation ID for tracking the request
    /// </summary>
    [JsonPropertyName("correlationId")]
    public string CorrelationId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the initial processing status
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "accepted";

    /// <summary>
    /// Gets or sets the URL to check processing status
    /// </summary>
    [JsonPropertyName("statusUrl")]
    public string StatusUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the estimated processing time
    /// </summary>
    [JsonPropertyName("estimatedProcessingTime")]
    public TimeSpan EstimatedProcessingTime { get; set; }

    /// <summary>
    /// Estimated UTC completion time, computed as <c>UtcNow + <see cref="EstimatedProcessingTime"/></c>.
    /// </summary>
    [JsonPropertyName("estimatedCompletion")]
    public DateTime? EstimatedCompletion => DateTime.UtcNow.Add(EstimatedProcessingTime);
}

/// <summary>
/// Model representing the status of async processing
/// </summary>
public class ProcessingStatus
{
    /// <summary>
    /// Gets or sets the correlation ID for tracking
    /// </summary>
    [JsonPropertyName("correlationId")]
    public string CorrelationId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the processing status (pending, processing, completed, failed)
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "pending";

    /// <summary>
    /// Gets or sets the progress percentage (0-100)
    /// </summary>
    [JsonPropertyName("progress")]
    public int Progress { get; set; }

    /// <summary>
    /// Gets or sets the number of items processed
    /// </summary>
    [JsonPropertyName("processedCount")]
    public int ProcessedCount { get; set; }

    /// <summary>
    /// Gets or sets the total number of items to process
    /// </summary>
    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }

    /// <summary>
    /// Gets or sets when processing started
    /// </summary>
    [JsonPropertyName("startedAt")]
    public DateTime StartedAt { get; set; }

    /// <summary>
    /// Gets or sets when processing completed (null if not completed)
    /// </summary>
    [JsonPropertyName("completedAt")]
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Gets or sets any errors that occurred during processing
    /// </summary>
    [JsonPropertyName("errors")]
    public List<string> Errors { get; set; } = new();

    /// <summary>
    /// Gets or sets the results of processing (only included when completed)
    /// </summary>
    [JsonPropertyName("results")]
    public object? Results { get; set; }
}

/// <summary>
/// Response model for processing status queries
/// </summary>
public class ProcessingStatusResponse : ProcessingStatus
{
    // Inherits all properties from ProcessingStatus
    // Can be extended with additional response-specific properties if needed
}
