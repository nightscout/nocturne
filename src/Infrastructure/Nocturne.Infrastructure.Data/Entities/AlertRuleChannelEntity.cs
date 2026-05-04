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

    /// <summary>Display ordering within the rule's channel list. Not load-bearing.</summary>
    [Column("sort_order")]
    public int SortOrder { get; set; }

    /// <summary>When the channel configuration was created.</summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation

    /// <summary>Navigation back to the owning rule.</summary>
    public AlertRuleEntity? AlertRule { get; set; }
}
