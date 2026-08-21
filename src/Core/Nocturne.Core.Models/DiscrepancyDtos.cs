using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// Data transfer object for discrepancy analysis results
/// </summary>
/// <seealso cref="DiscrepancyDetailDto"/>
/// <seealso cref="DiscrepancySeverity"/>
/// <seealso cref="DiscrepancyType"/>
public class DiscrepancyAnalysisDto
{
    /// <summary>Unique identifier for this analysis record</summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Trace identifier linking the Nightscout and Nocturne requests being compared.
    /// The wire name stays <c>correlationId</c>: this DTO is both a read response of
    /// <c>GET /api/v4/discrepancy/analyses</c> and the payload one Nocturne posts to another's
    /// <c>POST /api/v4/discrepancy/ingest</c>, so a mixed-version pair has to agree on it.
    /// </summary>
    [JsonPropertyName("correlationId")]
    public string TraceId { get; set; } = string.Empty;

    /// <summary>When the analysis was performed</summary>
    public DateTimeOffset AnalysisTimestamp { get; set; }

    /// <summary>HTTP method of the compared request (e.g., "GET", "POST")</summary>
    public string RequestMethod { get; set; } = string.Empty;

    /// <summary>Request path that was compared (e.g., "/api/v1/entries")</summary>
    public string RequestPath { get; set; } = string.Empty;

    /// <summary>
    /// Overall match score as an integer percentage (0-100).
    /// 100 indicates a perfect match; lower values indicate increasing divergence.
    /// </summary>
    public int OverallMatch { get; set; }

    /// <summary>Whether the HTTP status codes matched between Nightscout and Nocturne</summary>
    public bool StatusCodeMatch { get; set; }

    /// <summary>Whether the response bodies matched between Nightscout and Nocturne</summary>
    public bool BodyMatch { get; set; }

    /// <summary>HTTP status code returned by the legacy Nightscout instance</summary>
    public int? NightscoutStatusCode { get; set; }

    /// <summary>HTTP status code returned by Nocturne</summary>
    public int? NocturneStatusCode { get; set; }

    /// <summary>Response time of the legacy Nightscout instance in milliseconds</summary>
    public long? NightscoutResponseTimeMs { get; set; }

    /// <summary>Response time of Nocturne in milliseconds</summary>
    public long? NocturneResponseTimeMs { get; set; }

    /// <summary>Total time taken to compare both responses in milliseconds</summary>
    public long TotalProcessingTimeMs { get; set; }

    /// <summary>Human-readable summary of the comparison result</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>Which response was selected to be returned to the client ("nightscout" or "nocturne")</summary>
    public string? SelectedResponseTarget { get; set; }

    /// <summary>Reason the <see cref="SelectedResponseTarget"/> was chosen</summary>
    public string? SelectionReason { get; set; }

    /// <summary>Number of <see cref="DiscrepancySeverity.Critical"/> discrepancies found</summary>
    public int CriticalDiscrepancyCount { get; set; }

    /// <summary>Number of <see cref="DiscrepancySeverity.Major"/> discrepancies found</summary>
    public int MajorDiscrepancyCount { get; set; }

    /// <summary>Number of <see cref="DiscrepancySeverity.Minor"/> discrepancies found</summary>
    public int MinorDiscrepancyCount { get; set; }

    /// <summary>Whether the Nightscout instance failed to return a response</summary>
    public bool NightscoutMissing { get; set; }

    /// <summary>Whether Nocturne failed to return a response</summary>
    public bool NocturneMissing { get; set; }

    /// <summary>Error message if the comparison itself failed</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Individual discrepancies found during the comparison</summary>
    public List<DiscrepancyDetailDto> Discrepancies { get; set; } = new();
}

/// <summary>
/// DTO for discrepancies forwarded from remote Nocturne instances
/// </summary>
public class ForwardedDiscrepancyDto
{
    /// <summary>
    /// Source identifier for the Nocturne instance that forwarded this discrepancy
    /// </summary>
    public string SourceId { get; set; } = string.Empty;

    /// <summary>
    /// Timestamp when the discrepancy was received by the remote instance
    /// </summary>
    public DateTimeOffset ReceivedAt { get; set; }

    /// <summary>
    /// The discrepancy analysis data
    /// </summary>
    public DiscrepancyAnalysisDto Analysis { get; set; } = new();
}

/// <summary>
/// Data transfer object for detailed discrepancy information
/// </summary>
/// <seealso cref="DiscrepancyAnalysisDto"/>
/// <seealso cref="DiscrepancyType"/>
/// <seealso cref="DiscrepancySeverity"/>
public class DiscrepancyDetailDto
{
    /// <summary>Unique identifier for this discrepancy detail record</summary>
    public Guid Id { get; set; }

    /// <summary>Category of discrepancy (status code, body, header, etc.)</summary>
    public DiscrepancyType DiscrepancyType { get; set; }

    /// <summary>Severity level of this discrepancy</summary>
    public DiscrepancySeverity Severity { get; set; }

    /// <summary>JSON path or field name where the discrepancy was found (e.g., "entries[0].sgv")</summary>
    public string Field { get; set; } = string.Empty;

    /// <summary>Value from the legacy Nightscout response (serialized as a string for comparison)</summary>
    public string NightscoutValue { get; set; } = string.Empty;

    /// <summary>Value from the Nocturne response (serialized as a string for comparison)</summary>
    public string NocturneValue { get; set; } = string.Empty;

    /// <summary>Human-readable description of the discrepancy</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>When this discrepancy detail was recorded</summary>
    public DateTimeOffset RecordedAt { get; set; }
}

/// <summary>
/// Real-time compatibility status for dashboard
/// </summary>
public class CompatibilityStatus
{
    /// <summary>Overall compatibility score as a percentage (0-100), where 100 is full compatibility</summary>
    public double OverallScore { get; set; }

    /// <summary>Total number of requests that have been compared</summary>
    public int TotalRequests { get; set; }

    /// <summary>Health status string (e.g., "healthy", "degraded", "critical")</summary>
    public string HealthStatus { get; set; } = string.Empty;

    /// <summary>When this status was last recalculated</summary>
    public DateTimeOffset LastUpdated { get; set; }

    /// <summary>Number of open <see cref="DiscrepancySeverity.Critical"/> issues</summary>
    public int CriticalIssues { get; set; }

    /// <summary>Number of open <see cref="DiscrepancySeverity.Major"/> issues</summary>
    public int MajorIssues { get; set; }

    /// <summary>Number of open <see cref="DiscrepancySeverity.Minor"/> issues</summary>
    public int MinorIssues { get; set; }
}



/// <summary>
/// Type of response match
/// </summary>
public enum ResponseMatchType
{
    /// <summary>
    /// Responses match perfectly
    /// </summary>
    Perfect,

    /// <summary>
    /// Minor differences found
    /// </summary>
    MinorDifferences,

    /// <summary>
    /// Major differences found
    /// </summary>
    MajorDifferences,

    /// <summary>
    /// Critical differences found
    /// </summary>
    CriticalDifferences,

    /// <summary>
    /// Nightscout response is missing
    /// </summary>
    NightscoutMissing,

    /// <summary>
    /// Nocturne response is missing
    /// </summary>
    NocturneMissing,

    /// <summary>
    /// Both responses are missing
    /// </summary>
    BothMissing,

    /// <summary>
    /// Error occurred during comparison
    /// </summary>
    ComparisonError,
}

/// <summary>
/// Type of discrepancy found during comparison
/// </summary>
public enum DiscrepancyType
{
    /// <summary>
    /// HTTP status code differs
    /// </summary>
    StatusCode,

    /// <summary>
    /// Response header differs
    /// </summary>
    Header,

    /// <summary>
    /// Content type differs
    /// </summary>
    ContentType,

    /// <summary>
    /// Response body differs
    /// </summary>
    Body,

    /// <summary>
    /// JSON structure differs
    /// </summary>
    JsonStructure,

    /// <summary>
    /// String value differs
    /// </summary>
    StringValue,

    /// <summary>
    /// Numeric value differs
    /// </summary>
    NumericValue,

    /// <summary>
    /// Timestamp differs
    /// </summary>
    Timestamp,

    /// <summary>
    /// Array length differs
    /// </summary>
    ArrayLength,

    /// <summary>
    /// Performance metrics differ significantly
    /// </summary>
    Performance,
}

/// <summary>
/// Severity level of a discrepancy
/// </summary>
public enum DiscrepancySeverity
{
    /// <summary>
    /// Minor difference that likely doesn't affect functionality
    /// </summary>
    Minor,

    /// <summary>
    /// Major difference that might affect functionality
    /// </summary>
    Major,

    /// <summary>
    /// Critical difference that likely affects functionality
    /// </summary>
    Critical,
}
