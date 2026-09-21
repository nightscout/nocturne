using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// A manually-entered lab HbA1c result (DCCT/NGSP %), kept only to compare against the computed
/// eHbA1c estimate on the eHbA1c report — never read by that calculation.
/// </summary>
[Table("lab_hba1c_results")]
public class LabHbA1cResultEntity : ITenantScoped, ISoftDeletable
{
    /// <summary>Owning tenant for RLS isolation.</summary>
    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    /// <summary>Primary key (UUID v7).</summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>Local calendar date the blood was drawn, stored at UTC midnight.</summary>
    [Column("measured_at")]
    public DateTime MeasuredAt { get; set; }

    /// <summary>Lab-reported HbA1c in DCCT/NGSP percent.</summary>
    [Column("value_percent")]
    public double ValuePercent { get; set; }

    /// <summary>Optional free-text note (e.g. lab name).</summary>
    [Column("note")]
    [MaxLength(500)]
    public string? Note { get; set; }

    /// <summary>When this row was created.</summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    /// <summary>When this row was soft-deleted, or null while it is live.</summary>
    [Column("deleted_at")]
    public DateTime? DeletedAt { get; set; }
}
