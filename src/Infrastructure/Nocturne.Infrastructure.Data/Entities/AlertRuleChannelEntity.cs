using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// A delivery channel attached directly to an <see cref="AlertRuleEntity"/>.
/// Replaces the schedule/escalation-step/step-channel chain with a flat per-rule list.
/// Channels are dispatched in parallel when the rule fires; ordering is cosmetic only.
/// </summary>
[Table("alert_rule_channels")]
public class AlertRuleChannelEntity : ITenantScoped
{
    /// <summary>Unique identifier for the channel.</summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>Identifier of the tenant this channel belongs to.</summary>
    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    /// <summary>Identifier of the alert rule this channel is attached to.</summary>
    [Column("alert_rule_id")]
    public Guid AlertRuleId { get; set; }

    /// <summary>
    /// Channel kind. Mirrors <see cref="ChannelType"/> (e.g. <c>web_push</c>, <c>in_app</c>,
    /// <c>webhook</c>, chat-bot variants).
    /// </summary>
    [Column("channel_type")]
    [MaxLength(32)]
    public ChannelType ChannelType { get; set; }

    /// <summary>
    /// Destination address. Empty for channels that don't need one (e.g. <c>in_app</c>,
    /// <c>web_push</c> when keyed by subject); URL/handle/key otherwise.
    /// </summary>
    [Column("destination")]
    [MaxLength(512)]
    public string Destination { get; set; } = string.Empty;

    /// <summary>Human-readable label for the destination (e.g. "Mom's phone").</summary>
    [Column("destination_label")]
    [MaxLength(128)]
    public string? DestinationLabel { get; set; }

    /// <summary>
    /// HMAC signing secret for a <c>webhook</c> channel, held as ciphertext from
    /// <c>ISecretEncryptionService</c>. Null when the channel is not a webhook, or when its
    /// receiver does not verify signatures. It lives in its own column rather than in
    /// <see cref="Metadata"/> because metadata is echoed back verbatim on read.
    /// </summary>
    /// <remarks>
    /// Sized so the API's 256-character cap cannot overflow it whatever script the secret is in:
    /// a BMP character costs one UTF-16 unit but up to three UTF-8 bytes, so 256 characters reach
    /// 768 bytes, which AES-GCM frames to 768 + 12 nonce + 16 tag = 796 bytes and Base64 widens to
    /// 1064 characters. The stated contract is the tighter 256-byte one the controller enforces;
    /// this width is what keeps the looser character cap from ever reaching the column.
    /// </remarks>
    [Column("secret")]
    [MaxLength(1536)]
    public string? Secret { get; set; }

    /// <summary>Display ordering within the rule's channel list. Not load-bearing.</summary>
    [Column("sort_order")]
    public int SortOrder { get; set; }

    /// <summary>When the channel configuration was created.</summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Channel-specific configuration stored as JSONB. Schema varies by channel type.
    /// For home_assistant: { "allow_ack": bool, "delivery_methods": string[], "critical_push": bool }
    /// </summary>
    [Column("metadata", TypeName = "jsonb")]
    public string? Metadata { get; set; }

    // Navigation

    /// <summary>Navigation back to the owning rule.</summary>
    public AlertRuleEntity? AlertRule { get; set; }
}
