using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Alerts;

/// <summary>
/// Repository port for alert rule configuration, alert instances, and excursion state.
/// Provides the persistence layer consumed by <see cref="IAlertOrchestrator"/>.
/// </summary>
/// <seealso cref="IAlertOrchestrator"/>
/// <seealso cref="IExcursionTracker"/>
public interface IAlertRepository
{
    /// <summary>
    /// Returns all enabled <see cref="AlertRuleSnapshot"/> records for the specified tenant.
    /// </summary>
    /// <param name="tenantId">The tenant whose rules should be retrieved.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A read-only list of enabled alert rule snapshots.</returns>
    Task<IReadOnlyList<AlertRuleSnapshot>> GetEnabledRulesAsync(Guid tenantId, CancellationToken ct);

    /// <summary>
    /// Returns the flat per-rule channel list. Channels are dispatched in parallel when the
    /// rule fires; <see cref="AlertRuleChannelSnapshot.SortOrder"/> is cosmetic only.
    /// </summary>
    Task<IReadOnlyList<AlertRuleChannelSnapshot>> GetChannelsForRuleAsync(Guid ruleId, CancellationToken ct);

    /// <summary>
    /// Creates a new <see cref="AlertInstanceSnapshot"/> for a triggered alert.
    /// </summary>
    Task<AlertInstanceSnapshot> CreateInstanceAsync(CreateAlertInstanceRequest request, CancellationToken ct);

    /// <summary>
    /// Returns all alert instances associated with a specific excursion.
    /// </summary>
    /// <param name="excursionId">The <see cref="AlertExcursion"/> identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A read-only list of alert instances for the excursion.</returns>
    Task<IReadOnlyList<AlertInstanceSnapshot>> GetInstancesForExcursionAsync(Guid excursionId, CancellationToken ct);

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
    /// <param name="ct">Cancellation token.</param>
    Task ResolveInstancesForExcursionAsync(Guid excursionId, DateTime resolvedAt, string? resolutionReason, CancellationToken ct);

    /// <summary>
    /// Returns every open excursion whose owning rule has auto-resolve enabled
    /// and a non-null <c>AutoResolveParams</c>. Used by <c>AlertSweepService</c>
    /// to evaluate auto-resolve conditions that don't depend on the latest
    /// reading (e.g. time-of-day, IOB, sensor age).
    /// </summary>
    Task<IReadOnlyList<AutoResolveExcursionSnapshot>> GetAutoResolveExcursionsAsync(CancellationToken ct);

    /// <summary>
    /// Returns the distinct InApp delivery <c>Destination</c> values (user identifiers) that
    /// received any delivery for this excursion's instances. Used by
    /// <c>ExcursionResolutionHandler</c> to auto-archive in-app notifications when the
    /// excursion closes.
    /// </summary>
    Task<IReadOnlyList<string>> GetInAppDestinationsForExcursionAsync(Guid excursionId, CancellationToken ct);

    /// <summary>
    /// Updates an existing alert instance (e.g., advancing its escalation step or snooze state).
    /// </summary>
    /// <param name="request">The update request containing the fields to change.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the instance has been updated.</returns>
    Task UpdateInstanceAsync(UpdateAlertInstanceRequest request, CancellationToken ct);

    /// <summary>
    /// Expires all pending (unsent) deliveries for the specified alert instances,
    /// typically called when instances are resolved or acknowledged.
    /// </summary>
    /// <param name="instanceIds">The alert instance identifiers whose pending deliveries should be expired.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the deliveries have been expired.</returns>
    Task ExpirePendingDeliveriesAsync(IReadOnlyList<Guid> instanceIds, CancellationToken ct);

    /// <summary>
    /// Counts the number of active (unresolved) excursions for a tenant.
    /// </summary>
    /// <param name="tenantId">The tenant identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The count of active excursions.</returns>
    Task<int> CountActiveExcursionsAsync(Guid tenantId, CancellationToken ct);

    /// <summary>
    /// Returns all excursions that are currently in a hysteresis cool-down period.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A read-only list of excursion snapshots in hysteresis.</returns>
    Task<IReadOnlyList<HysteresisExcursionSnapshot>> GetExcursionsInHysteresisAsync(CancellationToken ct);

    /// <summary>
    /// Returns the tenant-level alert context (global mute state, timezone, etc.) used
    /// by the orchestrator to evaluate scheduling rules.
    /// </summary>
    /// <param name="tenantId">The tenant identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The tenant alert context, or <c>null</c> if the tenant has no alert configuration.</returns>
    Task<TenantAlertContext?> GetTenantAlertContextAsync(Guid tenantId, CancellationToken ct);

    /// <summary>
    /// Returns the tenant's <c>tenant_alert_settings</c> row as a snapshot, or
    /// <see cref="TenantAlertSettingsSnapshot.Empty"/> when no row exists.
    /// Used by <see cref="ISensorContextEnricher"/> to populate
    /// <see cref="SensorContext.ActiveDoNotDisturb"/> per evaluation pass.
    /// </summary>
    Task<TenantAlertSettingsSnapshot> GetTenantAlertSettingsAsync(Guid tenantId, CancellationToken ct);

    /// <summary>
    /// Marks an alert instance as suppressed at fire time without dispatching deliveries.
    /// Writes <paramref name="reason"/> to <c>alert_instances.suppression_reason</c> so Replay
    /// and History can display "would have fired but suppressed" rows. Currently the only
    /// reason emitted by the orchestrator is <c>"dnd"</c>.
    /// </summary>
    /// <param name="tenantId">Tenant scope for the lookup. Defence-in-depth alongside RLS.</param>
    Task MarkInstanceSuppressedAsync(Guid tenantId, Guid instanceId, string reason, CancellationToken ct);

    /// <summary>
    /// Returns all enabled signal-loss detection rules across all tenants.
    /// Used by background services that monitor for stale sensor data.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A read-only list of enabled signal-loss rule snapshots.</returns>
    Task<IReadOnlyList<SignalLossRuleSnapshot>> GetEnabledSignalLossRulesAsync(CancellationToken ct);

    /// <summary>
    /// Returns the latest glucose trend rate (mg/dL per minute) for the tenant,
    /// used by rate-of-change alert conditions.
    /// </summary>
    /// <param name="tenantId">The tenant identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The latest trend rate, or <c>null</c> if no trend data is available.</returns>
    Task<double?> GetLatestTrendRateAsync(Guid tenantId, CancellationToken ct);

    /// <summary>
    /// Returns snoozed alert instances whose snooze period has expired as of
    /// <paramref name="asOf"/>, so they can resume escalation.
    /// </summary>
    /// <param name="asOf">The point-in-time cutoff for expired snoozes.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A read-only list of previously snoozed instance snapshots.</returns>
    Task<IReadOnlyList<SnoozedInstanceSnapshot>> GetExpiredSnoozedInstancesAsync(DateTime asOf, CancellationToken ct);

    /// <summary>
    /// Returns a snapshot of every active <see cref="AlertExcursion"/> for the tenant, keyed by
    /// the <see cref="AlertRule"/> id that owns it. Used by <c>alert_state</c> conditions to
    /// reference live alerts cross-rule.
    /// </summary>
    /// <param name="tenantId">The tenant identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// Dictionary keyed by alert rule id. Each value's <see cref="ActiveAlertSnapshot.State"/> is
    /// always <c>"firing"</c>; downstream evaluators decide acknowledgement state from
    /// <see cref="ActiveAlertSnapshot.AcknowledgedAt"/>.
    /// </returns>
    Task<IReadOnlyDictionary<Guid, ActiveAlertSnapshot>> GetActiveAlertSnapshotsAsync(
        Guid tenantId, CancellationToken ct);

    /// <summary>
    /// Persists all pending changes tracked by the underlying context.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when changes have been saved.</returns>
    Task SaveChangesAsync(CancellationToken ct);
}
