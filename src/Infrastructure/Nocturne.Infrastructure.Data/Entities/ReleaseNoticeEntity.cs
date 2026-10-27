using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// A release notice this instance has sent, one row per kind, version and tenant owner. Not
/// tenant-scoped: the release check reads and writes it for every tenant. The row outlives the
/// notification it sent, so an owner who dismisses a notice is not sent it again.
/// </summary>
[Table("release_notices")]
public class ReleaseNoticeEntity : ISystemCreated
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    /// <summary>
    /// <c>available</c> for a newer release, <c>installed</c> for the release now running.
    /// </summary>
    [Required]
    [MaxLength(32)]
    [Column("kind")]
    public string Kind { get; set; } = string.Empty;

    [Required]
    [MaxLength(64)]
    [Column("version")]
    public string Version { get; set; } = string.Empty;

    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    [Column("subject_id")]
    public Guid SubjectId { get; set; }

    [Column("sys_created_at")]
    public DateTime SysCreatedAt { get; set; }
}
