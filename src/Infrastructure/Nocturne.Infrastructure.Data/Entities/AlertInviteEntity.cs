using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// A shareable invite token that grants a follower permission to receive alerts
/// and optionally acknowledge them. Scoped to a single rule channel.
/// </summary>
[Table("alert_invites")]
public class AlertInviteEntity : ITenantScoped
{
    /// <summary>
    /// Unique identifier for the alert invite
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Identifier of the tenant this invite belongs to
    /// </summary>
    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    /// <summary>
    /// Identifier of the user who created this invite
    /// </summary>
    [Column("created_by")]
    public Guid CreatedBy { get; set; }

    /// <summary>
    /// Unique, URL-safe invite token.
    /// </summary>
    [Column("token")]
    [MaxLength(128)]
    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// Identifier of the rule channel this invite grants access to.
    /// </summary>
    [Column("alert_rule_channel_id")]
    public Guid AlertRuleChannelId { get; set; }

    /// <summary>
    /// Permission scope: "view_acknowledge" | "view_only"
    /// </summary>
    [Column("permission_scope")]
    [MaxLength(32)]
    public string PermissionScope { get; set; } = "view_acknowledge";

    /// <summary>
    /// Whether the invite has already been redeemed
    /// </summary>
    [Column("is_used")]
    public bool IsUsed { get; set; }

    /// <summary>
    /// Identifier of the follower who used the invite
    /// </summary>
    [Column("used_by")]
    public Guid? UsedBy { get; set; }

    /// <summary>
    /// When the invite expires
    /// </summary>
    [Column("expires_at")]
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// When the invite was created
    /// </summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation

    /// <summary>
    /// Navigation property to the associated rule channel.
    /// </summary>
    public AlertRuleChannelEntity? AlertRuleChannel { get; set; }
}
