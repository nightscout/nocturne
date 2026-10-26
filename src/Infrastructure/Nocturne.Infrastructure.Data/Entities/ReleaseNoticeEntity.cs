using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// A release notice this instance has sent to its tenant owners, one row per kind and version.
/// Instance-wide, no tenant scoping. The row outlives the notifications it sent, so an owner who
/// dismisses a notice is not sent it again.
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

    [Column("sys_created_at")]
    public DateTime SysCreatedAt { get; set; }
}
