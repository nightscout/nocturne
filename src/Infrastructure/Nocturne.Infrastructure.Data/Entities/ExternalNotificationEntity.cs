using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// One notice a sender outside Nocturne sent to one member of a tenant, under the sender's own key.
/// Kept after the in-app notification it created is cleaned up, so a key is sent to a member once,
/// and it is the audit trail of who sent what to whom and when it was withdrawn. Not tenant-scoped:
/// the operator sends from no tenant, and the row holds no text the sender wrote.
/// </summary>
[Table("external_notifications")]
public class ExternalNotificationEntity : ISystemCreated
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    /// <summary><c>operator</c> or <c>app</c>.</summary>
    [Required]
    [MaxLength(16)]
    [Column("sender_kind")]
    public string SenderKind { get; set; } = string.Empty;

    /// <summary><c>operator</c> for the operator, the OAuth <c>client_id</c> for an app.</summary>
    [Required]
    [MaxLength(255)]
    [Column("sender_id")]
    public string SenderId { get; set; } = string.Empty;

    /// <summary>The name the notice was shown under when it was sent.</summary>
    [Required]
    [MaxLength(255)]
    [Column("sender_name")]
    public string SenderName { get; set; } = string.Empty;

    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    /// <summary>The member the notice was sent to.</summary>
    [Column("subject_id")]
    public Guid SubjectId { get; set; }

    /// <summary>The sender's key for the notice, unique per sender, tenant and member.</summary>
    [Required]
    [MaxLength(100)]
    [Column("key")]
    public string Key { get; set; } = string.Empty;

    /// <summary>The in-app notification the send created; null until it is created.</summary>
    [Column("notification_id")]
    public Guid? NotificationId { get; set; }

    /// <summary>The OAuth grant an app sent under; null for the operator.</summary>
    [Column("grant_id")]
    public Guid? GrantId { get; set; }

    [Column("sys_created_at")]
    public DateTime SysCreatedAt { get; set; }

    /// <summary>When the sender withdrew the notice; null while it stands.</summary>
    [Column("withdrawn_at")]
    public DateTime? WithdrawnAt { get; set; }
}
