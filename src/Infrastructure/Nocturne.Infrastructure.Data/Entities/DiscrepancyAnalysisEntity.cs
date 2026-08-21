using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Nocturne.Core.Models;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// Entity for storing compatibility proxy response comparison results and discrepancy analysis
/// </summary>
[Table("discrepancy_analyses")]
public class DiscrepancyAnalysisEntity : ITenantScoped
{
    /// <summary>
    /// Identifier of the tenant this discrepancy analysis belongs to
    /// </summary>
    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    /// <summary>
    /// Unique identifier for the analysis
    /// </summary>
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    /// <summary>
    /// Request trace identifier for tracking
    /// </summary>
    [Column("correlation_id")]
    [MaxLength(128)]
    [Required]
    public string TraceId { get; set; } = string.Empty;

    /// <summary>
    /// Timestamp when the analysis was performed
    /// </summary>
    [Column("analysis_timestamp")]
    [Required]
    public DateTimeOffset AnalysisTimestamp { get; set; }

    /// <summary>
    /// HTTP method of the intercepted request
    /// </summary>
    [Column("request_method")]
    [MaxLength(10)]
    [Required]
    public string RequestMethod { get; set; } = string.Empty;

    /// <summary>
    /// Request path and query string
    /// </summary>
    [Column("request_path")]
    [MaxLength(2048)]
    [Required]
    public string RequestPath { get; set; } = string.Empty;

    /// <summary>
    /// Overall match assessment
    /// </summary>
    [Column("overall_match")]
    [Required]
    public ResponseMatchType OverallMatch { get; set; }

    /// <summary>
    /// Whether status codes match
    /// </summary>
    [Column("status_code_match")]
    [Required]
    public bool StatusCodeMatch { get; set; }

    /// <summary>
    /// Whether response bodies match
    /// </summary>
    [Column("body_match")]
    [Required]
    public bool BodyMatch { get; set; }

    /// <summary>
    /// Nightscout HTTP status code
    /// </summary>
    [Column("nightscout_status_code")]
    public int? NightscoutStatusCode { get; set; }

    /// <summary>
    /// Nocturne HTTP status code
    /// </summary>
    [Column("nocturne_status_code")]
    public int? NocturneStatusCode { get; set; }

    /// <summary>
    /// Nightscout response time in milliseconds
    /// </summary>
    [Column("nightscout_response_time_ms")]
    public long? NightscoutResponseTimeMs { get; set; }

    /// <summary>
    /// Nocturne response time in milliseconds
    /// </summary>
    [Column("nocturne_response_time_ms")]
    public long? NocturneResponseTimeMs { get; set; }

    /// <summary>
    /// Total processing time in milliseconds
    /// </summary>
    [Column("total_processing_time_ms")]
    [Required]
    public long TotalProcessingTimeMs { get; set; }

    /// <summary>
    /// Summary of comparison results
    /// </summary>
    [Column("summary")]
    [MaxLength(1000)]
    [Required]
    public string Summary { get; set; } = string.Empty;

    /// <summary>
    /// Selected response target (Nightscout or Nocturne)
    /// </summary>
    [Column("selected_response_target")]
    [MaxLength(50)]
    public string? SelectedResponseTarget { get; set; }

    /// <summary>
    /// Reason for response selection
    /// </summary>
    [Column("selection_reason")]
    [MaxLength(500)]
    public string? SelectionReason { get; set; }

    /// <summary>
    /// Count of critical discrepancies found
    /// </summary>
    [Column("critical_discrepancy_count")]
    [Required]
    public int CriticalDiscrepancyCount { get; set; }

    /// <summary>
    /// Count of major discrepancies found
    /// </summary>
    [Column("major_discrepancy_count")]
    [Required]
    public int MajorDiscrepancyCount { get; set; }

    /// <summary>
    /// Count of minor discrepancies found
    /// </summary>
    [Column("minor_discrepancy_count")]
    [Required]
    public int MinorDiscrepancyCount { get; set; }

    /// <summary>
    /// Navigation property for detailed discrepancies
    /// </summary>
    public virtual ICollection<DiscrepancyDetailEntity> Discrepancies { get; set; } =
        new List<DiscrepancyDetailEntity>();

    /// <summary>
    /// Whether Nightscout response was missing
    /// </summary>
    [Column("nightscout_missing")]
    [Required]
    public bool NightscoutMissing { get; set; }

    /// <summary>
    /// Whether Nocturne response was missing
    /// </summary>
    [Column("nocturne_missing")]
    [Required]
    public bool NocturneMissing { get; set; }

    /// <summary>
    /// Error message if comparison failed
    /// </summary>
    [Column("error_message")]
    [MaxLength(2000)]
    public string? ErrorMessage { get; set; }
}
