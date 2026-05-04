using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// A single delivery attempt for an alert instance through a specific channel.
/// Tracks payload, delivery status, platform IDs for threading, and retries.
/// </summary>
[Table("alert_deliveries")]
public class AlertDeliveryEntity : ITenantScoped
{
    /// <summary>
    /// Unique identifier for the alert delivery record
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// The unique identifier of the tenant this delivery belongs to
    /// </summary>
    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    /// <summary>
    /// Identifier of the alert instance being delivered
    /// </summary>
    [Column("alert_instance_id")]
    public Guid AlertInstanceId { get; set; }

    /// <summary>
    /// Identifier of the rule channel that produced this delivery, when known.
    /// Null on rows from the legacy schedule/escalation-step path.
    /// </summary>
    [Column("alert_rule_channel_id")]
    public Guid? AlertRuleChannelId { get; set; }

    /// <summary>
    /// Type of channel used for delivery (e.g., "email", "sms", "push")
    /// </summary>
    [Column("channel_type")]
    [MaxLength(32)]
    public ChannelType ChannelType { get; set; }

    /// <summary>
    /// Destination address or identifier for the delivery
    /// </summary>
    [Column("destination")]
    [MaxLength(512)]
    public string Destination { get; set; } = string.Empty;

    /// <summary>
    /// JSONB rendered payload sent to the channel adapter.
    /// </summary>
    [Column("payload", TypeName = "jsonb")]
    public string Payload { get; set; } = "{}";

    /// <summary>
    /// Delivery lifecycle status: "pending" | "delivered" | "failed" | "expired"
    /// </summary>
    [Column("status")]
    [MaxLength(16)]
    public string Status { get; set; } = "pending";

    /// <summary>
    /// Platform-specific message ID for update/thread operations.
    /// </summary>
    [Column("platform_message_id")]
    [MaxLength(256)]
    public string? PlatformMessageId { get; set; }

    /// <summary>
    /// Platform-specific thread ID for grouped messages.
    /// </summary>
    [Column("platform_thread_id")]
    [MaxLength(256)]
    public string? PlatformThreadId { get; set; }

    /// <summary>
    /// When the delivery attempt was created
    /// </summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the message was successfully delivered
    /// </summary>
    [Column("delivered_at")]
    public DateTime? DeliveredAt { get; set; }

    /// <summary>
    /// Number of delivery attempts made
    /// </summary>
    [Column("retry_count")]
    public int RetryCount { get; set; }

    /// <summary>
    /// Last error message encountered during delivery
    /// </summary>
    [Column("last_error")]
    public string? LastError { get; set; }

    /// <summary>
    /// True when this delivery was produced by a test-fire endpoint. Mirrors
    /// <see cref="AlertInstanceEntity.IsTest"/> on the parent instance — denormalised so
    /// History queries don't need to join the instance table.
    /// </summary>
    [Column("is_test")]
    public bool IsTest { get; set; }

    // Navigation

    /// <summary>
    /// Navigation property to the associated alert instance
    /// </summary>
    public AlertInstanceEntity? AlertInstance { get; set; }

    /// <summary>
    /// Navigation property to the associated rule channel, when set.
    /// </summary>
    public AlertRuleChannelEntity? AlertRuleChannel { get; set; }
}
