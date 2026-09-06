using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// A composable alert rule with a condition tree and optional auto-resolve behaviour.
/// Each rule owns schedules, which own escalation chains.
/// </summary>
[Table("alert_rules")]
public class AlertRuleEntity : ITenantScoped, IAuditable
{
    /// <summary>
    /// Unique identifier for the alert rule
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Identifier of the tenant this alert rule belongs to
    /// </summary>
    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    /// <summary>
    /// Display name of the alert rule
    /// </summary>
    [Column("name")]
    [MaxLength(128)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Detailed description of the alert rule
    /// </summary>
    [Column("description")]
    [MaxLength(512)]
    public string? Description { get; set; }

    /// <summary>
    /// Condition type discriminator.
    /// </summary>
    [Column("condition_type")]
    [MaxLength(32)]
    public AlertConditionType ConditionType { get; set; } = AlertConditionType.Threshold;

    /// <summary>
    /// JSONB condition parameters (thresholds, rates, durations, composite children, etc.)
    /// </summary>
    [Column("condition_params", TypeName = "jsonb")]
    public string ConditionParams { get; set; } = "{}";

    /// <summary>
    /// When true, the rule clears itself once its condition no longer holds (subject to <see cref="AutoResolveParams"/>).
    /// </summary>
    [Column("auto_resolve_enabled")]
    public bool AutoResolveEnabled { get; set; }

    /// <summary>
    /// JSONB auto-resolve parameters (e.g. delay, mode). Null when unused.
    /// </summary>
    [Column("auto_resolve_params", TypeName = "jsonb")]
    public string? AutoResolveParams { get; set; }

    /// <summary>
    /// Alert severity. Critical alerts bypass quiet hours.
    /// </summary>
    [Column("severity")]
    [MaxLength(16)]
    public AlertRuleSeverity Severity { get; set; } = AlertRuleSeverity.Warning;

    /// <summary>
    /// Client-side presentation config (audio, visual, snooze). Stored as JSONB.
    /// The server stores this but does not make decisions based on it.
    /// </summary>
    [Column("client_configuration", TypeName = "jsonb")]
    public string ClientConfiguration { get; set; } = "{}";

    /// <summary>
    /// Whether the alert rule is currently active
    /// </summary>
    [Column("is_enabled")]
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// When true, this rule still fires while the tenant is in Do Not Disturb mode (manual
    /// or scheduled). Critical-severity rules implicitly bypass DND regardless of this flag —
    /// the flag is only meaningful for non-critical rules the user wants to keep active.
    /// </summary>
    [Column("allow_through_dnd")]
    public bool AllowThroughDnd { get; set; }

    /// <summary>
    /// Low/high classification for scoped Do Not Disturb (ADR 0004), derived by the
    /// shared engine's <c>classify</c> from the directional leaves of
    /// <see cref="ConditionParams"/>. Computed on create/update (and backfilled once
    /// for pre-existing rules); a scoped <c>lows</c>/<c>highs</c> window silences a
    /// rule only when its class matches. Defaults to <see cref="RuleScopeClass.Undirected"/>
    /// (all-only) until classified.
    /// </summary>
    [Column("scope_class")]
    public RuleScopeClass ScopeClass { get; set; } = RuleScopeClass.Undirected;

    /// <summary>
    /// Owner tag for rules synthesised from another feature's configuration, e.g.
    /// <c>tracker:{definitionId}</c> for tracker notification thresholds. Null for
    /// user-authored rules. The owning feature overwrites the condition/name/severity
    /// on every sync and deletes the rule with its source config; deletion through the
    /// alert-rules API is rejected so the source config stays authoritative. Channels
    /// and client configuration are user-editable and survive syncs.
    /// </summary>
    [Column("managed_by")]
    [MaxLength(64)]
    public string? ManagedBy { get; set; }

    /// <summary>
    /// Order in which the rule should be processed or displayed
    /// </summary>
    [Column("sort_order")]
    public int SortOrder { get; set; }

    /// <summary>
    /// When the alert rule was created
    /// </summary>
    [AuditIgnored]
    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the alert rule was last updated
    /// </summary>
    [AuditIgnored]
    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation

    /// <summary>
    /// Flat list of delivery channels for this rule. When the rule fires, every channel in
    /// this collection receives a delivery in parallel.
    /// </summary>
    public ICollection<AlertRuleChannelEntity> Channels { get; set; } = [];

    /// <summary>
    /// Current state tracker for this alert rule
    /// </summary>
    public AlertTrackerStateEntity? TrackerState { get; set; }
}
