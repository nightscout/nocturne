using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// A one-time notice this instance has sent, one row per kind, version and tenant owner. Not
/// tenant-scoped: the background jobs that send notices read and write it for every tenant. The row
/// outlives the notification it sent, so an owner who dismisses a notice is not sent it again.
/// </summary>
[Table("owner_notices")]
public class OwnerNoticeEntity : ISystemCreated
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Required]
    [MaxLength(32)]
    [Column("kind")]
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// What the notice is about within its kind: a release version, or an instance key check
    /// generation.
    /// </summary>
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
