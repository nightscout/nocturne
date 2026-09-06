using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Nocturne.Core.Models;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// Entity for storing detailed discrepancy information
/// </summary>
[Table("discrepancy_details")]
public class DiscrepancyDetailEntity : ITenantScoped
{
    /// <summary>
    /// Identifier of the tenant this discrepancy detail belongs to
    /// </summary>
    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    /// <summary>
    /// Unique identifier for the discrepancy detail
    /// </summary>
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    /// <summary>
    /// Foreign key to the parent discrepancy analysis
    /// </summary>
    [Column("analysis_id")]
    [Required]
    public Guid AnalysisId { get; set; }

    /// <summary>
    /// Navigation property to parent analysis
    /// </summary>
    [ForeignKey(nameof(AnalysisId))]
    public virtual DiscrepancyAnalysisEntity Analysis { get; set; } = null!;

    /// <summary>
    /// Type of discrepancy
    /// </summary>
    [Column("discrepancy_type")]
    [Required]
    public DiscrepancyType DiscrepancyType { get; set; }

    /// <summary>
    /// Severity level of the discrepancy
    /// </summary>
    [Column("severity")]
    [Required]
    public DiscrepancySeverity Severity { get; set; }

    /// <summary>
    /// Field or path where discrepancy was found
    /// </summary>
    [Column("field")]
    [MaxLength(500)]
    [Required]
    public string Field { get; set; } = string.Empty;

    /// <summary>
    /// Value from Nightscout response
    /// </summary>
    [Column("nightscout_value")]
    [MaxLength(2000)]
    [Required]
    public string NightscoutValue { get; set; } = string.Empty;

    /// <summary>
    /// Value from Nocturne response
    /// </summary>
    [Column("nocturne_value")]
    [MaxLength(2000)]
    [Required]
    public string NocturneValue { get; set; } = string.Empty;

    /// <summary>
    /// Description of the discrepancy
    /// </summary>
    [Column("description")]
    [MaxLength(1000)]
    [Required]
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Timestamp when the discrepancy was recorded
    /// </summary>
    [Column("recorded_at")]
    [Required]
    public DateTimeOffset RecordedAt { get; set; }
}
