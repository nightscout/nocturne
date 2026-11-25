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
    /// Returns the enabled rules of the tenant as <see cref="GetEnabledRulesAsync"/> does, together
    /// with the enabled rules it leaves out because they cannot be read.
    /// </summary>
    Task<EnabledAlertRules> ReadEnabledRulesAsync(CancellationToken ct);

    /// <summary>
    /// Returns the enabled rules of <paramref name="tenantId"/> as <see cref="ReadEnabledRulesAsync(CancellationToken)"/>
    /// does.
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
    /// without its channels. A condition type, severity or scope class no member has reads as the
    /// stand-in the rule editor shows for it.
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
    /// The instance <paramref name="excursionId"/>'s opening raised, created
    /// <c>triggered</c> at <paramref name="triggeredAt"/> when it has none. Serialised per
    /// excursion across processes with <see cref="ResolveInstancesForExcursionAsync"/>, so callers
    /// acting on one opening at once raise one instance, and an opening and its close do not
    /// interleave.
    /// </summary>
    /// <param name="whileOpen">
    /// When true, null instead of an instance once the excursion has ended or no longer exists: a
    /// late opening must not raise an instance its close will never resolve.
    /// </param>
    Task<OpenedInstance?> EnsureInstanceAsync(
        Guid excursionId, DateTime triggeredAt, bool whileOpen, CancellationToken ct);

    /// <summary>
    /// The tenant's excursions of a condition whose opening or close still owes work
    /// (<c>IAlertTrackerRepository.CreateExcursionAsync</c>) and was last taken up before
    /// <paramref name="takenUpBefore"/>, oldest first.
    /// </summary>
    Task<IReadOnlyList<OwedTransition>> GetOwedTransitionsAsync(DateTime takenUpBefore, CancellationToken ct);

    /// <summary>
    /// Takes up the work <paramref name="kind"/> of <paramref name="excursionId"/> still owes, as of
    /// <paramref name="now"/>, when it was last taken up at <paramref name="seen"/>. False when
    /// another caller took it up or settled it since it was read.
    /// </summary>
    Task<bool> TakeUpOwedTransitionAsync(
        Guid excursionId, ExcursionTransitionType kind, DateTime seen, DateTime now, CancellationToken ct);

    /// <summary>Records that the work <paramref name="kind"/> of <paramref name="excursionId"/> owed is done.</summary>
    Task SettleTransitionAsync(Guid excursionId, ExcursionTransitionType kind, CancellationToken ct);

    /// <summary>
    /// Records that the close of <paramref name="excursionId"/> owes its work again, taken up at
    /// <paramref name="now"/>, when the excursion has ended. False when it is open or does not exist.
    /// </summary>
    Task<bool> OweCloseIfEndedAsync(Guid excursionId, DateTime now, CancellationToken ct);

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
    /// with the given resolution timestamp and reason, under the excursion's instance lock
    /// (<see cref="EnsureInstanceAsync"/>).
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
    /// Marks an alert instance as suppressed at fire time without dispatching deliveries.
    /// Writes <paramref name="reason"/> to <c>alert_instances.suppression_reason</c> so Replay
    /// and History can display "would have fired but suppressed" rows. Currently the only
    /// reason emitted by the orchestrator is <c>"dnd"</c>.
    /// </summary>
    Task MarkInstanceSuppressedAsync(Guid instanceId, string reason, CancellationToken ct);

    /// <summary>
    /// Records a notice that version <paramref name="conditionVersion"/> of
    /// <paramref name="rule"/>'s condition cannot be evaluated, unless one already stands for
    /// that version: an excursion row carrying the version, with no end until it is resolved, and
    /// one instance, unresolved while the notice stands. Serialised per rule across processes, so
    /// one version records one notice however many callers find it at once.
    /// </summary>
    /// <returns>
    /// The notice standing for the version, new or not, and whether it was dispatched (its
    /// opening owes no work, and it has a delivery row or Do Not Disturb suppressed it); null when
    /// the rule is not enabled, or its stored condition is no longer the one in <paramref name="rule"/>.
    /// </returns>
    Task<NoticeRecorded?> RecordUnevaluableNoticeAsync(
        AlertRuleSnapshot rule, string conditionVersion, DateTime at, CancellationToken ct);

    /// <summary>
    /// As <see cref="RecordUnevaluableNoticeAsync"/>, for a rule whose stored condition type or
    /// severity no member has: null when the rule is not enabled, or any of its stored columns
    /// named in <paramref name="rule"/> has changed.
    /// </summary>
    Task<NoticeRecorded?> RecordUnreadableRuleNoticeAsync(
        UnreadableAlertRule rule, string conditionVersion, DateTime at, CancellationToken ct);

    /// <summary>
    /// The notices that a rule cannot be evaluated that still stand (their instance is
    /// unresolved), of <paramref name="ruleId"/>, or of every rule of the tenant when null.
    /// </summary>
    Task<IReadOnlyList<StandingUnevaluableNotice>> GetStandingUnevaluableNoticesAsync(Guid? ruleId, CancellationToken ct);

    /// <summary>Ends a standing notice of either kind at <paramref name="at"/>, as it is resolved.</summary>
    Task EndNoticeAsync(Guid excursionId, DateTime at, CancellationToken ct);

    /// <summary>
    /// Records a notice that <paramref name="ruleId"/>'s hold that began at
    /// <paramref name="heldSince"/> reached the engine's bound, unless one already stands for that
    /// hold: an excursion row carrying the instant, with no end until it is resolved, and one
    /// instance. Serialised per rule across processes with the notice that a rule cannot be
    /// evaluated, so one hold records one notice however many callers find it at once.
    /// </summary>
    /// <returns>
    /// The notice standing for the hold, new or not, and whether it was dispatched; null when the
    /// rule is not enabled or its tracker state no longer holds since <paramref name="heldSince"/>.
    /// </returns>
    Task<NoticeRecorded?> RecordHeldNoticeAsync(Guid ruleId, DateTime heldSince, DateTime at, CancellationToken ct);

    /// <summary>
    /// The notices that a rule's hold reached the bound that still stand, of
    /// <paramref name="ruleId"/>, or of every rule of the tenant when null.
    /// </summary>
    Task<IReadOnlyList<StandingHeldNotice>> GetStandingHeldNoticesAsync(Guid? ruleId, CancellationToken ct);

    /// <summary>The tenant's rules whose stored tracker state records a hold.</summary>
    Task<IReadOnlyList<HeldRuleState>> GetHeldRuleStatesAsync(CancellationToken ct);

    /// <summary>Whether an excursion of the rule's condition was open at <paramref name="at"/>.</summary>
    Task<bool> HadExcursionOpenAtAsync(Guid ruleId, DateTime at, CancellationToken ct);

    /// <summary>
    /// The Home Assistant instances (OAuth client ids) that an excursion's instances were
    /// delivered to.
    /// </summary>
    Task<IReadOnlyList<string>> GetHomeAssistantInstancesSentExcursionAsync(Guid excursionId, CancellationToken ct);

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

/// <summary>
/// An excursion's opening or close whose work the host still owes: which transition
/// (<see cref="ExcursionTransitionType.ExcursionOpened"/> or
/// <see cref="ExcursionTransitionType.ExcursionClosed"/>), when the work was last taken up, and the
/// excursion's end as it stood when read.
/// </summary>
public record OwedTransition(
    Guid ExcursionId,
    Guid AlertRuleId,
    ExcursionTransitionType Kind,
    DateTime TakenUpAt,
    DateTime? EndedAt);
