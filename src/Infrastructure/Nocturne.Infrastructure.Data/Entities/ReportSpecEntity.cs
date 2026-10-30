using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// A saved custom report: a name and a report spec. A definition, not a time series, so it is
/// hidden from shares and read in full by a history-clamped member.
/// </summary>
[Table("report_specs")]
public class ReportSpecEntity : ITenantScoped, IIdentified, ISoftDeletable, IEntityTimestamped, IAuditable
{
    public const int MaxNameLength = 120;

    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    [Column("name")]
    [MaxLength(MaxNameLength)]
    public string Name { get; set; } = string.Empty;

    /// <summary>The normalised spec, in the format <see cref="Version"/> names.</summary>
    [Column("spec", TypeName = "jsonb")]
    public string Spec { get; set; } = "{}";

    [Column("version")]
    public int Version { get; set; }

    [Column("created_by_subject_id")]
    public Guid? CreatedBySubjectId { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }

    [Column("deleted_at")]
    public DateTime? DeletedAt { get; set; }
}
