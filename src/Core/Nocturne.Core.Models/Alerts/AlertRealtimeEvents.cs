namespace Nocturne.Core.Models.Alerts;

/// <summary>
/// The <c>alert_resolved</c> realtime message: an excursion closed. Sent to the tenant's alert
/// subscribers and Home Assistant instances.
/// </summary>
public record AlertResolvedEvent
{
    /// <summary>The tenant the excursion belongs to.</summary>
    public required Guid TenantId { get; init; }

    /// <summary>The <see cref="AlertExcursion"/> that closed.</summary>
    public required Guid ExcursionId { get; init; }

    /// <summary>The <see cref="AlertRule"/> the excursion belongs to.</summary>
    public required Guid AlertRuleId { get; init; }

    /// <summary>The rule's name when the excursion closed.</summary>
    public required string RuleName { get; init; }

    /// <summary>When the excursion's instances were resolved.</summary>
    public required DateTime ResolvedAt { get; init; }

    /// <summary>Why the excursion closed; null only when the close carried no reason.</summary>
    public required ExcursionCloseReason? Reason { get; init; }
}

/// <summary>
/// The <c>alert_acknowledged</c> realtime message: one or more excursions were acknowledged for
/// everyone. Sent to the tenant's alert subscribers and Home Assistant instances. A member's mute
/// of an excursion for themselves sends none.
/// </summary>
public record AlertAcknowledgedEvent
{
    /// <summary>The tenant the excursions belong to.</summary>
    public required Guid TenantId { get; init; }

    /// <summary>
    /// Every <see cref="AlertExcursion"/> the acknowledgement covered: one for a single
    /// acknowledgement, each active excursion of the tenant for an acknowledge-all.
    /// </summary>
    public required IReadOnlyList<Guid> ExcursionIds { get; init; }

    /// <summary>Who acknowledged.</summary>
    public required string AcknowledgedBy { get; init; }

    /// <summary>When the acknowledgement was recorded.</summary>
    public required DateTime AcknowledgedAt { get; init; }
}
