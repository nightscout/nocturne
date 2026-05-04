using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Entities.V4;

/// <summary>
/// Groups V4 records that were decomposed from the same source record.
/// Deleting a batch cascades to all sibling V4 records.
/// </summary>
[Table("decomposition_batches")]
public class DecompositionBatchEntity : ITenantScoped
{
    /// <summary>Primary key (UUID v7).</summary>
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    /// <summary>Owning tenant for RLS isolation.</summary>
    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    /// <summary>
    /// What created this batch (treatment_decomposer, entry_decomposer, tidepool, etc.)
    /// </summary>
    [Column("source")]
    [MaxLength(128)]
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Legacy MongoDB _id of the source record for traceability
    /// </summary>
    [Column("source_record_id")]
    [MaxLength(128)]
    public string? SourceRecordId { get; set; }

    /// <summary>
    /// When the decomposition occurred
    /// </summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; }
}
