using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.Core.Contracts.Alerts;

/// <summary>
/// Repository port for alert rule configuration, alert instances, and excursion state.
/// Provides the persistence layer consumed by <see cref="IAlertOrchestrator"/>.
/// </summary>
/// <remarks>
/// A method runs as the scope's reader on its tenant unless it takes a tenant. One that takes a
/// tenant pins it in place of the scope's and keeps the scope's share and history-clamp state: a
/// loop calls it once per tenant from a scope that has none.
/// </remarks>
/// <seealso cref="IAlertOrchestrator"/>
/// <seealso cref="IExcursionTracker"/>
public interface IAlertRepository
{
    /// <summary>
    /// Returns all enabled <see cref="AlertRuleSnapshot"/> records of the tenant.
    /// </summary>
    Task<IReadOnlyList<AlertRuleSnapshot>> GetEnabledRulesAsync(CancellationToken ct);

    /// <summary>
    /// Returns the enabled rules of <paramref name="tenantId"/> as <see cref="GetEnabledRulesAsync"/>
    /// does, together with the ids of the enabled rules it leaves out because they cannot be read.
    /// </summary>
    /// <remarks>A startup loop calls it once per tenant.</remarks>
    Task<EnabledAlertRules> ReadEnabledRulesAsync(Guid tenantId, CancellationToken ct);

    /// <summary>
    /// Returns every rule of the tenant, enabled or not, in <c>SortOrder</c>, each with its channels.
    /// </summary>
    /// <remarks>
    /// A viewer without an account, such as a public share or a guest link, could read rules and
    /// their channel destinations through it. A caller serving a request must refuse those viewers
    /// first, as <c>AlertRulesController</c> does with <c>[DenyNoAccountViewer]</c>.
    /// </remarks>
    Task<IReadOnlyList<AlertRuleRecord>> GetRulesAsync(CancellationToken ct);

    /// <summary>
    /// Returns every rule of <paramref name="tenantId"/>, enabled or not, in <c>SortOrder</c>,
    /// without its channels.
    /// </summary>
    /// <remarks>
    /// A startup loop calls it once per tenant. Callers serving a request are bound as for
    /// <see cref="GetRulesAsync"/>.
    /// </remarks>
    Task<IReadOnlyList<AlertRuleSnapshot>> GetRuleSnapshotsAsync(Guid tenantId, CancellationToken ct);

    /// <summary>
    /// Returns one rule of the tenant, enabled or not, with its channels; null when the tenant has
    /// no rule <paramref name="ruleId"/>.
    /// </summary>
    /// <remarks>Callers are bound as for <see cref="GetRulesAsync"/>.</remarks>
    Task<AlertRuleRecord?> GetRuleAsync(Guid ruleId, CancellationToken ct);

    /// <summary>
    /// Counts the tenant's rules, enabled or not.
    /// </summary>
    /// <remarks>Callers are bound as for <see cref="GetRulesAsync"/>.</remarks>
    Task<int> CountRulesAsync(CancellationToken ct);

    /// <summary>
    /// Returns the open, unacknowledged excursions of the rules whose <c>ManagedBy</c> tag is
    /// <paramref name="managedBy"/>.
    /// </summary>
    /// <remarks>Callers are bound as for <see cref="GetRulesAsync"/>.</remarks>
    Task<IReadOnlyList<Guid>> GetUnacknowledgedExcursionIdsAsync(string managedBy, CancellationToken ct);

    /// <summary>
    /// Creates a new <see cref="AlertInstanceSnapshot"/> for a triggered alert.
    /// </summary>
    Task<AlertInstanceSnapshot> CreateInstanceAsync(CreateAlertInstanceRequest request, CancellationToken ct);

    /// <param name="excursionId">The <see cref="AlertExcursion"/> identifier.</param>
    /// <returns>A read-only list of alert instances for the excursion.</returns>
    Task<IReadOnlyList<AlertInstanceSnapshot>> GetInstancesForExcursionAsync(Guid excursionId, CancellationToken ct);

    /// <summary>
    /// The rule an excursion belongs to, or null when the excursion does not exist.
    /// </summary>
    /// <param name="excursionId">The <see cref="AlertExcursion"/> identifier.</param>
    Task<ExcursionRuleSnapshot?> GetExcursionRuleAsync(Guid excursionId, CancellationToken ct);

    /// <summary>
    /// Resolves all active alert instances for the specified excursion, marking them
    /// with the given resolution timestamp and reason.
    /// </summary>
    /// <param name="excursionId">The <see cref="AlertExcursion"/> identifier.</param>
    /// <param name="resolvedAt">The timestamp when the excursion was resolved.</param>
    /// <param name="resolutionReason">
    /// Wire string from <see cref="Nocturne.Core.Models.Alerts.ExcursionCloseReason"/>
    /// (e.g. <c>"hysteresis"</c>, <c>"auto"</c>). Null is allowed only as a defensive
    /// fall-back — every production call site has a reason.
    /// </param>
    Task ResolveInstancesForExcursionAsync(Guid excursionId, DateTime resolvedAt, string? resolutionReason, CancellationToken ct);

    /// <summary>
    /// Returns every open excursion of the tenant whose owning rule has auto-resolve enabled
    /// and a non-null <c>AutoResolveParams</c>.
    /// </summary>
    Task<IReadOnlyList<AutoResolveExcursionSnapshot>> GetAutoResolveExcursionsAsync(CancellationToken ct);

    /// <summary>
    /// Returns the distinct InApp delivery <c>Destination</c> values (user identifiers) that
    /// received any delivery for this excursion's instances. Used by
    /// <c>ExcursionResolutionHandler</c> to auto-archive in-app notifications when the
    /// excursion closes.
    /// </summary>
    /// <param name="excursionId">The <see cref="AlertExcursion"/> identifier.</param>
    Task<IReadOnlyList<string>> GetInAppDestinationsForExcursionAsync(Guid excursionId, CancellationToken ct);

    /// <summary>
    /// Updates an existing alert instance of <see cref="UpdateAlertInstanceRequest.TenantId"/> (e.g.,
    /// advancing its escalation step or snooze state).
    /// </summary>
    /// <remarks>The sweep calls it once per tenant.</remarks>
    /// <param name="request">The update request containing the fields to change.</param>
    Task UpdateInstanceAsync(UpdateAlertInstanceRequest request, CancellationToken ct);

    /// <summary>
    /// Expires all pending (unsent) deliveries for the specified alert instances,
    /// typically called when instances are resolved or acknowledged.
    /// </summary>
    /// <param name="instanceIds">The alert instance identifiers whose pending deliveries should be expired.</param>
    Task ExpirePendingDeliveriesAsync(IReadOnlyList<Guid> instanceIds, CancellationToken ct);

    /// <summary>
    /// Deletes every member's mute of an excursion, so no mute outlives the excursion it silenced.
    /// </summary>
    /// <param name="excursionId">The closed excursion.</param>
    Task DeleteExcursionMutesAsync(Guid excursionId, CancellationToken ct);

    /// <summary>
    /// Counts the number of active (unresolved) excursions of the tenant.
    /// </summary>
    Task<int> CountActiveExcursionsAsync(CancellationToken ct);

    /// <summary>
    /// Returns the tenant's excursions that are in a hysteresis cool-down period.
    /// </summary>
    /// <returns>A read-only list of excursion snapshots in hysteresis.</returns>
    Task<IReadOnlyList<HysteresisExcursionSnapshot>> GetExcursionsInHysteresisAsync(CancellationToken ct);

    /// <summary>
    /// Returns the tenant-level alert context (global mute state, timezone, etc.) used
    /// by the orchestrator to evaluate scheduling rules.
    /// </summary>
    /// <returns>The tenant alert context, or <c>null</c> if the tenant has no alert configuration.</returns>
    Task<TenantAlertContext?> GetTenantAlertContextAsync(Guid tenantId, CancellationToken ct);

    /// <summary>
    /// Returns the tenant's <c>tenant_alert_settings</c> row as a snapshot, or
    /// <see cref="TenantAlertSettingsSnapshot.Empty"/> when no row exists.
    /// Used by <see cref="ISensorContextEnricher"/> to populate
    /// <see cref="SensorContext.ActiveDoNotDisturb"/> per evaluation pass.
    /// </summary>
    Task<TenantAlertSettingsSnapshot> GetTenantAlertSettingsAsync(CancellationToken ct);

    /// <summary>
    /// Returns the tenant's DND windows that can still take effect at or after
    /// <paramref name="nowUtc"/> — uncleared (<c>cleared_at IS NULL</c>) <b>and</b> not past
    /// their expiry — as resolver snapshots, for scoped Do Not Disturb (ADR 0004 D5). Every
    /// <see cref="DateTime"/> is normalised to <see cref="DateTimeKind.Utc"/> so
    /// <see cref="DndWindowSnapshot.IsActiveAt"/> (naive comparison) is sound.
    /// </summary>
    /// <remarks>
    /// The expiry bound is what keeps this bounded. A window that simply runs out is never
    /// cleared — <c>cleared_at</c> stays null for audit — so filtering on <c>cleared_at</c>
    /// alone would return every timed mute the tenant has ever set, growing without limit on a
    /// path that runs once per glucose reading. The partial index on
    /// <c>(tenant_id, scope) WHERE cleared_at IS NULL</c> still serves the lookup; only rows
    /// that can still matter come back.
    /// </remarks>
    Task<IReadOnlyList<DndWindowSnapshot>> GetUnexpiredDndWindowsAsync(DateTime nowUtc, CancellationToken ct);

    /// <summary>
    /// Returns every DND window for the tenant received by <paramref name="asOfReceiptUtc"/>
    /// (<c>created_at &lt;= asOfReceiptUtc</c>), <b>including cleared/expired ones</b>, for replay
    /// (ADR 0004 D5). Replay resolves each window with
    /// <see cref="DndWindowSnapshot.WasActiveAt"/> (receipt-gated) per tick, so cleared windows'
    /// <c>cleared_at</c> still matters. Every <see cref="DateTime"/> is normalised to
    /// <see cref="DateTimeKind.Utc"/>.
    /// </summary>
    Task<IReadOnlyList<DndWindowSnapshot>> GetDndWindowsAsOfAsync(DateTime asOfReceiptUtc, CancellationToken ct);

    /// <summary>
    /// Marks an alert instance as suppressed at fire time without dispatching deliveries.
    /// Writes <paramref name="reason"/> to <c>alert_instances.suppression_reason</c> so Replay
    /// and History can display "would have fired but suppressed" rows. Currently the only
    /// reason emitted by the orchestrator is <c>"dnd"</c>.
    /// </summary>
    Task MarkInstanceSuppressedAsync(Guid instanceId, string reason, CancellationToken ct);

    /// <summary>
    /// Returns the tenant's snoozed alert instances whose snooze period has expired as of
    /// <paramref name="asOf"/>, so they can resume escalation.
    /// </summary>
    /// <param name="asOf">The point-in-time cutoff for expired snoozes.</param>
    /// <returns>A read-only list of previously snoozed instance snapshots.</returns>
    Task<IReadOnlyList<SnoozedInstanceSnapshot>> GetExpiredSnoozedInstancesAsync(DateTime asOf, CancellationToken ct);

    /// <summary>
    /// Returns a snapshot of every active <see cref="AlertExcursion"/> for the tenant, keyed by
    /// the <see cref="AlertRule"/> id that owns it. Used by <c>alert_state</c> conditions to
    /// reference live alerts cross-rule.
    /// </summary>
    /// <returns>
    /// Dictionary keyed by alert rule id. Each value's <see cref="ActiveAlertSnapshot.State"/> is
    /// always <c>"firing"</c>; downstream evaluators decide acknowledgement state from
    /// <see cref="ActiveAlertSnapshot.AcknowledgedAt"/>.
    /// </returns>
    Task<IReadOnlyDictionary<Guid, ActiveAlertSnapshot>> GetActiveAlertSnapshotsAsync(CancellationToken ct);
}
