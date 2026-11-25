using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data.Converters;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// Repository for alert orchestration queries and mutations.
/// Methods are virtual to allow mocking with CallBase in tests.
/// </summary>
public class AlertRepository : IAlertRepository
{
    private readonly INocturneDbContextFactory _contexts;
    private readonly ILogger<AlertRepository> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AlertRepository"/> class.
    /// </summary>
    /// <param name="contexts">The database context factory.</param>
    /// <param name="logger">Reports enabled rules that cannot be read.</param>
    public AlertRepository(
        INocturneDbContextFactory contexts,
        ILogger<AlertRepository>? logger = null)
    {
        _contexts = contexts;
        _logger = logger ?? NullLogger<AlertRepository>.Instance;
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<AlertRuleSnapshot>> GetEnabledRulesAsync(CancellationToken ct) =>
        (await ReadEnabledRulesAsync(ct)).Rules;

    /// <inheritdoc/>
    public virtual async Task<EnabledAlertRules> ReadEnabledRulesAsync(CancellationToken ct)
    {
        await using var context = await _contexts.CreateAsync(ct);

        return await ReadEnabledRulesAsync(context, ct);
    }

    /// <inheritdoc/>
    public virtual async Task<EnabledAlertRules> ReadEnabledRulesAsync(Guid tenantId, CancellationToken ct)
    {
        await using var context = await _contexts.CreateForTenantAsync(tenantId, ct);

        return await ReadEnabledRulesAsync(context, ct);
    }

    /// <summary>
    /// The context's tenant's enabled rules, less any whose condition type, severity or scope class no enum
    /// member has (<see cref="AlertRuleMapper.IsReadable(AlertRuleEntity)"/>).
    /// </summary>
    /// <remarks>
    /// Each rule left out is logged as an error every time it is read, and returned as an
    /// <see cref="UnreadableAlertRule"/>, with its stored values as text, for the sweep to tell its
    /// recipients.
    /// </remarks>
    private async Task<EnabledAlertRules> ReadEnabledRulesAsync(NocturneDbContext context, CancellationToken ct)
    {
        var enabled = await context.AlertRules
            .AsNoTracking()
            .Where(r => r.IsEnabled)
            .OrderBy(r => r.SortOrder)
            .ToListAsync(ct);
        var held = enabled.Where(r => !AlertRuleMapper.IsReadable(r)).ToDictionary(r => r.Id);
        if (held.Count == 0)
            return new EnabledAlertRules(enabled.Select(AlertRuleMapper.ToSnapshot).ToList(), []);

        var tenantId = context.TenantId;
        var stored = await context.Database
            .SqlQuery<StoredRule>(
                $"""SELECT id AS "Id", name AS "Name", condition_type AS "ConditionType", condition_params::text AS "ConditionParams", severity AS "Severity", scope_class AS "ScopeClass", allow_through_dnd AS "AllowThroughDnd" FROM alert_rules WHERE tenant_id = {tenantId} AND is_enabled""")
            .ToListAsync(ct);
        var unreadable = stored
            .Where(r => held.ContainsKey(r.Id))
            .Select(r => new UnreadableAlertRule(
                r.Id,
                r.Name,
                r.ConditionType,
                r.ConditionParams,
                r.Severity,
                AlertRuleMapper.Readable(held[r.Id].Severity),
                r.ScopeClass,
                AlertRuleMapper.Readable(held[r.Id].ScopeClass),
                r.AllowThroughDnd))
            .ToList();

        foreach (var rule in unreadable)
        {
            _logger.LogError(
                "Alert rule {AlertRuleId} for tenant {TenantId} is not evaluated: condition type '{ConditionType}', severity '{Severity}' or scope class '{ScopeClass}' is not one the engine knows",
                rule.Id, tenantId, rule.StoredConditionType, rule.StoredSeverity, rule.StoredScopeClass);
        }

        return new EnabledAlertRules(
            enabled.Where(r => !held.ContainsKey(r.Id)).Select(AlertRuleMapper.ToSnapshot).ToList(), unreadable);
    }

    private sealed class StoredRule
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public string ConditionType { get; init; } = string.Empty;
        public string ConditionParams { get; init; } = string.Empty;
        public string Severity { get; init; } = string.Empty;
        public string ScopeClass { get; init; } = string.Empty;
        public bool AllowThroughDnd { get; init; }
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<AlertRuleRecord>> GetRulesAsync(CancellationToken ct)
    {
        await using var context = await _contexts.CreateAsync(ct);

        var rules = await RulesWithChannels(context)
            .OrderBy(r => r.SortOrder)
            .ToListAsync(ct);
        return rules.Select(ToRecord).ToList();
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<AlertRuleSnapshot>> GetRuleSnapshotsAsync(
        Guid tenantId, CancellationToken ct)
    {
        await using var context = await _contexts.CreateForTenantAsync(tenantId, ct);

        var rules = await context.AlertRules
            .AsNoTracking()
            .OrderBy(r => r.SortOrder)
            .ToListAsync(ct);
        return rules.Select(AlertRuleMapper.ToSnapshot).ToList();
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<Guid>> GetUnacknowledgedExcursionIdsAsync(
        string managedBy, CancellationToken ct)
    {
        await using var context = await _contexts.CreateAsync(ct);

        return await context.AlertExcursions
            .AsNoTracking()
            .OfCondition(includeTest: true)
            .Where(e => e.EndedAt == null && e.AcknowledgedAt == null)
            .Join(
                context.AlertRules.Where(r => r.ManagedBy == managedBy),
                e => e.AlertRuleId,
                r => r.Id,
                (e, r) => e.Id)
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public virtual async Task<AlertRuleRecord?> GetRuleAsync(Guid ruleId, CancellationToken ct)
    {
        await using var context = await _contexts.CreateAsync(ct);

        var rule = await RulesWithChannels(context).FirstOrDefaultAsync(r => r.Id == ruleId, ct);
        return rule is null ? null : ToRecord(rule);
    }

    /// <inheritdoc/>
    public virtual async Task<int> CountRulesAsync(CancellationToken ct)
    {
        await using var context = await _contexts.CreateAsync(ct);

        return await context.AlertRules.CountAsync(ct);
    }

    private static IQueryable<AlertRuleEntity> RulesWithChannels(NocturneDbContext context) =>
        context.AlertRules
            .AsNoTracking()
            .Include(r => r.Channels);

    private static AlertRuleRecord ToRecord(AlertRuleEntity r) => AlertRuleMapper.ToRecord(r);

    private const int OpenedInstanceLockClass = 0x4E4F_494E;

    /// <inheritdoc/>
    /// <remarks>See <see cref="AdvisoryLockExtensions"/>.</remarks>
    public virtual async Task<OpenedInstance?> EnsureInstanceAsync(
        Guid excursionId, DateTime triggeredAt, bool whileOpen, CancellationToken ct)
    {
        await using var context = await _contexts.CreateAsync(ct);
        return await context.ExecuteInTransactionAsync<OpenedInstance?>(async token =>
        {
            await context.LockForTransactionAsync(OpenedInstanceLockClass, excursionId, token);

            if (whileOpen && !await context.AlertExcursions.AnyAsync(e => e.Id == excursionId && e.EndedAt == null, token))
                return null;

            var raised = await context.AlertInstances
                .AsNoTracking()
                .Where(i => i.AlertExcursionId == excursionId && !i.IsTest)
                .OrderBy(i => i.TriggeredAt)
                .Select(i => new OpenedInstance(i.Id, i.SuppressionReason != null))
                .FirstOrDefaultAsync(token);
            if (raised is not null)
                return raised;

            var entity = new AlertInstanceEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = context.TenantId,
                AlertExcursionId = excursionId,
                Status = "triggered",
                TriggeredAt = triggeredAt,
            };
            context.AlertInstances.Add(entity);
            await context.SaveChangesAsync(token);
            return new OpenedInstance(entity.Id, Suppressed: false);
        },
        verifySucceeded: async (raised, token) =>
            raised is null || await context.AlertInstances.AnyAsync(i => i.Id == raised.Id, token),
        ct: ct);
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<OwedTransition>> GetOwedTransitionsAsync(
        DateTime takenUpBefore, CancellationToken ct)
    {
        await using var context = await _contexts.CreateAsync(ct);

        var rows = await context.AlertExcursions
            .AsNoTracking()
            .OfCondition()
            .Where(e => e.OpenedOwedAt < takenUpBefore || e.ClosedOwedAt < takenUpBefore)
            .Select(e => new { e.Id, e.AlertRuleId, e.OpenedOwedAt, e.ClosedOwedAt, e.EndedAt })
            .ToListAsync(ct);

        var owed = new List<OwedTransition>();
        foreach (var row in rows)
        {
            if (row.OpenedOwedAt is { } opened && opened < takenUpBefore)
                owed.Add(new OwedTransition(
                    row.Id, row.AlertRuleId, ExcursionTransitionType.ExcursionOpened, opened, row.EndedAt));
            if (row.ClosedOwedAt is { } closed && closed < takenUpBefore)
                owed.Add(new OwedTransition(
                    row.Id, row.AlertRuleId, ExcursionTransitionType.ExcursionClosed, closed, row.EndedAt));
        }
        return owed.OrderBy(t => t.TakenUpAt).ToList();
    }

    /// <inheritdoc/>
    public virtual async Task<bool> TakeUpOwedTransitionAsync(
        Guid excursionId, ExcursionTransitionType kind, DateTime seen, DateTime now, CancellationToken ct)
    {
        await using var context = await _contexts.CreateAsync(ct);

        var excursion = context.AlertExcursions.Where(e => e.Id == excursionId);
        var taken = kind == ExcursionTransitionType.ExcursionOpened
            ? await excursion.Where(e => e.OpenedOwedAt == seen)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.OpenedOwedAt, now), ct)
            : await excursion.Where(e => e.ClosedOwedAt == seen)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.ClosedOwedAt, now), ct);
        return taken == 1;
    }

    /// <inheritdoc/>
    public virtual async Task SettleTransitionAsync(
        Guid excursionId, ExcursionTransitionType kind, CancellationToken ct)
    {
        await using var context = await _contexts.CreateAsync(ct);

        var excursion = await context.AlertExcursions.FirstOrDefaultAsync(e => e.Id == excursionId, ct);
        if (excursion is null)
            return;
        if (kind == ExcursionTransitionType.ExcursionOpened)
            excursion.OpenedOwedAt = null;
        else
            excursion.ClosedOwedAt = null;
        await context.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public virtual async Task<bool> OweCloseIfEndedAsync(Guid excursionId, DateTime now, CancellationToken ct)
    {
        await using var context = await _contexts.CreateAsync(ct);

        var excursion = await context.AlertExcursions.OfCondition(includeTest: true)
            .FirstOrDefaultAsync(e => e.Id == excursionId, ct);
        if (excursion?.EndedAt is null)
            return false;
        excursion.ClosedOwedAt = now;
        await context.SaveChangesAsync(ct);
        return true;
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<AlertInstanceSnapshot>> GetInstancesForExcursionAsync(
        Guid excursionId, CancellationToken ct)
    {
        await using var context = await _contexts.CreateAsync(ct);

        return await context.AlertInstances
            .AsNoTracking()
            .Where(i => i.AlertExcursionId == excursionId)
            .Select(i => new AlertInstanceSnapshot(
                i.Id, i.TenantId, i.AlertExcursionId,
                i.Status, i.TriggeredAt,
                i.SnoozedUntil, i.SnoozeCount))
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public virtual async Task<ExcursionRuleSnapshot?> GetExcursionRuleAsync(
        Guid excursionId, CancellationToken ct)
    {
        await using var context = await _contexts.CreateAsync(ct);

        return await context.AlertExcursions
            .AsNoTracking()
            .Where(e => e.Id == excursionId)
            .Join(context.AlertRules, e => e.AlertRuleId, r => r.Id,
                (e, r) => new ExcursionRuleSnapshot(
                    r.Id, r.Name, e.UnevaluableConditionVersion != null && e.HeldSince == null, e.HeldSince != null))
            .FirstOrDefaultAsync(ct);
    }

    /// <inheritdoc/>
    public virtual async Task ResolveInstancesForExcursionAsync(
        Guid excursionId, DateTime resolvedAt, string? resolutionReason, CancellationToken ct)
    {
        await using var context = await _contexts.CreateAsync(ct);

        await context.ExecuteInTransactionAsync(async token =>
        {
            await context.LockForTransactionAsync(OpenedInstanceLockClass, excursionId, token);
            await context.AlertInstances
                .Where(i => i.AlertExcursionId == excursionId
                            && i.Status != "resolved")
                .ExecuteUpdateAsync(s => s
                    .SetProperty(i => i.Status, "resolved")
                    .SetProperty(i => i.ResolvedAt, resolvedAt)
                    .SetProperty(i => i.ResolutionReason, resolutionReason), token);
        }, ct);
    }

    /// <inheritdoc/>
    public virtual async Task UpdateInstanceAsync(
        UpdateAlertInstanceRequest request, CancellationToken ct)
    {
        await using var context = await _contexts.CreateForTenantAsync(request.TenantId, ct);

        var entity = await context.AlertInstances
            .FirstOrDefaultAsync(i => i.Id == request.Id, ct);

        if (entity == null) return;

        if (request.Status is not null)
            entity.Status = request.Status;

        if (request.SnoozedUntil.HasValue)
            entity.SnoozedUntil = request.SnoozedUntil == DateTime.MinValue
                ? null : request.SnoozedUntil.Value;

        if (request.SnoozeCount.HasValue)
            entity.SnoozeCount = request.SnoozeCount.Value;

        await context.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public virtual async Task ExpirePendingDeliveriesAsync(
        IReadOnlyList<Guid> instanceIds, CancellationToken ct)
    {
        if (instanceIds.Count == 0) return;

        await using var context = await _contexts.CreateAsync(ct);

        await context.AlertDeliveries
            .Where(d => instanceIds.Contains(d.AlertInstanceId)
                        && d.Status == "pending")
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status, "expired"), ct);
    }

    /// <inheritdoc/>
    public virtual async Task DeleteExcursionMutesAsync(Guid excursionId, CancellationToken ct)
    {
        await using var context = await _contexts.CreateAsync(ct);

        await context.AlertExcursionMutes
            .Where(m => m.AlertExcursionId == excursionId)
            .ExecuteDeleteAsync(ct);
    }

    /// <inheritdoc/>
    public virtual async Task<int> CountActiveExcursionsAsync(CancellationToken ct)
    {
        await using var context = await _contexts.CreateAsync(ct);

        return await context.AlertExcursions
            .AsNoTracking()
            .OfCondition(includeTest: true)
            .Where(e => e.EndedAt == null)
            .CountAsync(ct);
    }

    /// <summary>
    /// Gets alert excursions that are currently in the hysteresis (recovery) period.
    /// </summary>
    /// <remarks>
    /// Selected by the rule's tracker state, which the tracker decides from, rather than by the
    /// excursion's copy of the hysteresis start: a state from before that copy existed is in
    /// hysteresis with no start on either row.
    /// </remarks>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of hysteresis excursion snapshots.</returns>
    public virtual async Task<IReadOnlyList<HysteresisExcursionSnapshot>> GetExcursionsInHysteresisAsync(
        CancellationToken ct)
    {
        await using var context = await _contexts.CreateAsync(ct);

        return await context.AlertTrackerState
            .AsNoTracking()
            .Where(s => s.State == "hysteresis" && s.ActiveExcursionId != null)
            .Select(s => new HysteresisExcursionSnapshot(
                s.ActiveExcursionId!.Value, s.TenantId, s.AlertRuleId, s.HysteresisStartedAt))
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<string>> GetInAppDestinationsForExcursionAsync(
        Guid excursionId, CancellationToken ct)
    {
        await using var context = await _contexts.CreateAsync(ct);

        return await DestinationsForExcursion(context, excursionId, ChannelType.InApp).ToListAsync(ct);
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<string>> GetHomeAssistantInstancesSentExcursionAsync(
        Guid excursionId, CancellationToken ct)
    {
        await using var context = await _contexts.CreateAsync(ct);

        return await DestinationsForExcursion(context, excursionId, ChannelType.HomeAssistant, deliveredOnly: true)
            .ToListAsync(ct);
    }

    private static IQueryable<string> DestinationsForExcursion(
        NocturneDbContext context, Guid excursionId, ChannelType channelType, bool deliveredOnly = false) =>
        context.AlertDeliveries
            .AsNoTracking()
            .Where(d => d.ChannelType == channelType && (!deliveredOnly || d.Status == "delivered"))
            .Join(context.AlertInstances.AsNoTracking(),
                d => d.AlertInstanceId, i => i.Id, (d, i) => new { d, i })
            .Where(x => x.i.AlertExcursionId == excursionId
                        && !string.IsNullOrEmpty(x.d.Destination))
            .Select(x => x.d.Destination)
            .Distinct();

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<AutoResolveExcursionSnapshot>> GetAutoResolveExcursionsAsync(
        CancellationToken ct)
    {
        await using var context = await _contexts.CreateAsync(ct);

        var excursions = await context.AlertExcursions
            .AsNoTracking()
            .OfCondition(includeTest: true)
            .Where(e => e.EndedAt == null)
            .Join(context.AlertRules.AsNoTracking(),
                e => e.AlertRuleId, r => r.Id, (e, r) => new { e, r })
            .Where(x => x.r.IsEnabled
                        && x.r.AutoResolveEnabled
                        && x.r.AutoResolveParams != null)
            .Select(x => new { x.e.Id, x.e.TenantId, Rule = x.r })
            .ToListAsync(ct);
        return excursions
            .Where(x => AlertRuleMapper.IsReadable(x.Rule))
            .Select(x => new AutoResolveExcursionSnapshot(x.Id, x.TenantId, AlertRuleMapper.ToSnapshot(x.Rule)))
            .ToList();
    }

    /// <inheritdoc/>
    public virtual async Task<TenantAlertContext?> GetTenantAlertContextAsync(
        Guid tenantId, CancellationToken ct)
    {
        await using var context = await _contexts.CreateForTenantAsync(tenantId, ct);

        var tenant = await context.Tenants
            .AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => new { t.Id, t.Slug, t.DisplayName, t.IsActive, t.LastReadingAt })
            .FirstOrDefaultAsync(ct);

        if (tenant is null) return null;

        var preferredName = await context.PatientRecords
            .AsNoTracking()
            .Select(p => p.PreferredName)
            .FirstOrDefaultAsync(ct);

        return new TenantAlertContext(
            tenant.Id, string.IsNullOrWhiteSpace(preferredName) ? null : preferredName, tenant.Slug, tenant.DisplayName,
            tenant.IsActive, tenant.LastReadingAt);
    }

    /// <inheritdoc/>
    public virtual async Task MarkInstanceSuppressedAsync(
        Guid instanceId, string reason, CancellationToken ct)
    {
        await using var context = await _contexts.CreateAsync(ct);

        var instance = await context.AlertInstances
            .FirstOrDefaultAsync(i => i.Id == instanceId, ct);
        if (instance is null) return;

        // Idempotency: if a suppression reason is already stamped, leave it alone — a
        // second source (e.g. a future quiet-hours bypass code path) shouldn't clobber
        // the original. First writer wins.
        if (instance.SuppressionReason is not null) return;

        instance.SuppressionReason = reason;
        // Suppression closes the active dispatch path. Keep the row around with
        // status="triggered" so Acknowledge / Resolve still work normally.
        await context.SaveChangesAsync(ct);
    }

    private const int NoticeLockClass = 0x4E55_4E56;

    /// <inheritdoc/>
    /// <remarks>
    /// The rule is read again under the lock: a disable commits before its retirement resolves the
    /// rule's notices, so either this sees the rule disabled and records nothing, or the retirement
    /// sees the notice this records. An edit made since the caller read the rule likewise records
    /// nothing for the condition it replaced. See <see cref="AdvisoryLockExtensions"/>.
    /// </remarks>
    public virtual Task<NoticeRecorded?> RecordUnevaluableNoticeAsync(
        AlertRuleSnapshot rule, string conditionVersion, DateTime at, CancellationToken ct) =>
        RecordNoticeAsync(
            rule.Id,
            e => e.UnevaluableConditionVersion = conditionVersion,
            async (context, token) => await Standing(context, rule.Id, conditionVersion).FirstOrDefaultAsync(token) is { } n
                ? new NoticeRecorded(n.ExcursionId, n.InstanceId, n.Dispatched)
                : null,
            at,
            (context, token) => context.AlertRules.AnyAsync(r => r.Id == rule.Id
                                                                 && r.IsEnabled
                                                                 && r.ConditionType == rule.ConditionType
                                                                 && r.ConditionParams == rule.ConditionParams, token),
            ct);

    /// <inheritdoc/>
    /// <remarks>
    /// The rule is read by raw SQL, since no entity can hold its condition type or severity.
    /// </remarks>
    public virtual Task<NoticeRecorded?> RecordUnreadableRuleNoticeAsync(
        UnreadableAlertRule rule, string conditionVersion, DateTime at, CancellationToken ct) =>
        RecordNoticeAsync(
            rule.Id,
            e => e.UnevaluableConditionVersion = conditionVersion,
            async (context, token) => await Standing(context, rule.Id, conditionVersion).FirstOrDefaultAsync(token) is { } n
                ? new NoticeRecorded(n.ExcursionId, n.InstanceId, n.Dispatched)
                : null,
            at,
            async (context, token) => (await context.Database
                .SqlQuery<Guid>(
                    $"""SELECT id AS "Value" FROM alert_rules WHERE id = {rule.Id} AND tenant_id = {context.TenantId} AND is_enabled AND condition_type = {rule.StoredConditionType} AND severity = {rule.StoredSeverity} AND scope_class = {rule.StoredScopeClass} AND condition_params::text = {rule.ConditionParams}""")
                .ToListAsync(token)).Count > 0,
            ct);

    /// <inheritdoc/>
    /// <remarks>
    /// The rule and its tracker state are read again under the lock, which a disable takes before
    /// its retirement resolves the rule's notices, as for <see cref="RecordUnevaluableNoticeAsync"/>.
    /// </remarks>
    public virtual Task<NoticeRecorded?> RecordHeldNoticeAsync(
        Guid ruleId, DateTime heldSince, DateTime at, CancellationToken ct) =>
        RecordNoticeAsync(
            ruleId,
            e =>
            {
                e.HeldSince = heldSince;
                e.UnevaluableConditionVersion = $"held:{heldSince:O}";
            },
            async (context, token) => await StandingHeld(context, ruleId, heldSince).FirstOrDefaultAsync(token) is { } n
                ? new NoticeRecorded(n.ExcursionId, n.InstanceId, n.Dispatched)
                : null,
            at,
            async (context, token) =>
                await context.AlertRules.AnyAsync(r => r.Id == ruleId && r.IsEnabled, token)
                && await context.AlertTrackerState.AnyAsync(s => s.AlertRuleId == ruleId && s.HeldSince == heldSince, token),
            ct);

    /// <summary>
    /// Records the notice <paramref name="mark"/> marks under the rule's notice lock, unless
    /// <paramref name="standing"/> finds it standing or the rule no longer holds what the caller read.
    /// </summary>
    private async Task<NoticeRecorded?> RecordNoticeAsync(
        Guid ruleId,
        Action<AlertExcursionEntity> mark,
        Func<NocturneDbContext, CancellationToken, Task<NoticeRecorded?>> standing,
        DateTime at,
        Func<NocturneDbContext, CancellationToken, Task<bool>> ruleStillHoldsWhatTheRunRead,
        CancellationToken ct)
    {
        await using var context = await _contexts.CreateAsync(ct);
        return await context.ExecuteInTransactionAsync<NoticeRecorded?>(async token =>
        {
            await context.LockForTransactionAsync(NoticeLockClass, ruleId, token);

            if (!await ruleStillHoldsWhatTheRunRead(context, token))
                return null;

            if (await standing(context, token) is { } current)
                return current;

            var excursion = new AlertExcursionEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = context.TenantId,
                AlertRuleId = ruleId,
                StartedAt = at,
                EndedAt = AlertExcursionEntity.NoticeStandingEnd,
                OpenedOwedAt = at,
            };
            mark(excursion);
            var instance = new AlertInstanceEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = context.TenantId,
                AlertExcursionId = excursion.Id,
                Status = "triggered",
                TriggeredAt = at,
            };
            context.AlertExcursions.Add(excursion);
            context.AlertInstances.Add(instance);
            await context.SaveChangesAsync(token);

            return new NoticeRecorded(excursion.Id, instance.Id, Dispatched: false);
        },
        verifySucceeded: async (recorded, token) =>
            recorded is not null
            && await context.AlertExcursions.StandingNotices().AnyAsync(e => e.Id == recorded.ExcursionId, token),
        ct: ct);
    }

    /// <inheritdoc/>
    public virtual async Task EndNoticeAsync(Guid excursionId, DateTime at, CancellationToken ct)
    {
        await using var context = await _contexts.CreateAsync(ct);

        var notice = await context.AlertExcursions
            .StandingNotices()
            .FirstOrDefaultAsync(e => e.Id == excursionId && e.EndedAt > at, ct);
        if (notice is null) return;

        notice.EndedAt = at;
        notice.OpenedOwedAt = null;
        await context.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<StandingUnevaluableNotice>> GetStandingUnevaluableNoticesAsync(
        Guid? ruleId, CancellationToken ct)
    {
        await using var context = await _contexts.CreateAsync(ct);

        return await Standing(context, ruleId).ToListAsync(ct);
    }

    private static IQueryable<StandingUnevaluableNotice> Standing(
        NocturneDbContext context, Guid? ruleId, string? conditionVersion = null) =>
        context.AlertInstances
            .AsNoTracking()
            .OfStandingNotices()
            .Where(i => i.AlertExcursion!.HeldSince == null)
            .Where(i => ruleId == null || i.AlertExcursion!.AlertRuleId == ruleId)
            .Where(i => conditionVersion == null || i.AlertExcursion!.UnevaluableConditionVersion == conditionVersion)
            .Select(i => new StandingUnevaluableNotice(
                i.AlertExcursionId,
                i.Id,
                i.AlertExcursion!.AlertRuleId,
                i.AlertExcursion.UnevaluableConditionVersion!,
                i.AlertExcursion.OpenedOwedAt == null
                && (i.SuppressionReason != null || context.AlertDeliveries.Any(d => d.AlertInstanceId == i.Id))));

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<StandingHeldNotice>> GetStandingHeldNoticesAsync(
        Guid? ruleId, CancellationToken ct)
    {
        await using var context = await _contexts.CreateAsync(ct);

        return await StandingHeld(context, ruleId).ToListAsync(ct);
    }

    private static IQueryable<StandingHeldNotice> StandingHeld(
        NocturneDbContext context, Guid? ruleId, DateTime? heldSince = null) =>
        context.AlertInstances
            .AsNoTracking()
            .OfStandingNotices()
            .Where(i => i.AlertExcursion!.HeldSince != null)
            .Where(i => ruleId == null || i.AlertExcursion!.AlertRuleId == ruleId)
            .Where(i => heldSince == null || i.AlertExcursion!.HeldSince == heldSince)
            .Select(i => new StandingHeldNotice(
                i.AlertExcursionId,
                i.Id,
                i.AlertExcursion!.AlertRuleId,
                i.AlertExcursion.HeldSince!.Value,
                i.AlertExcursion.OpenedOwedAt == null
                && (i.SuppressionReason != null || context.AlertDeliveries.Any(d => d.AlertInstanceId == i.Id))));

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<HeldRuleState>> GetHeldRuleStatesAsync(CancellationToken ct)
    {
        await using var context = await _contexts.CreateAsync(ct);

        return await context.AlertTrackerState
            .AsNoTracking()
            .Where(s => s.HeldSince != null)
            .Select(s => new HeldRuleState(s.AlertRuleId, s.HeldSince!.Value, s.UpdatedAt, s.HeldFacts, s.State))
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public virtual async Task<bool> HadExcursionOpenAtAsync(Guid ruleId, DateTime at, CancellationToken ct)
    {
        await using var context = await _contexts.CreateAsync(ct);

        return await context.AlertExcursions
            .AsNoTracking()
            .OfCondition()
            .AnyAsync(e => e.AlertRuleId == ruleId && e.StartedAt <= at && (e.EndedAt == null || e.EndedAt >= at), ct);
    }

    /// <summary>
    /// Gets alert instances whose snooze period has expired.
    /// </summary>
    /// <param name="asOf">The reference timestamp for determining if a snooze has expired.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of expired snoozed instances.</returns>
    public virtual async Task<IReadOnlyList<SnoozedInstanceSnapshot>> GetExpiredSnoozedInstancesAsync(
        DateTime asOf, CancellationToken ct)
    {
        await using var context = await _contexts.CreateAsync(ct);

        return await context.AlertInstances
            .AsNoTracking()
            .OfCondition(includeTest: true)
            .Where(i => i.SnoozedUntil != null
                        && i.SnoozedUntil <= asOf
                        && i.Status != "resolved")
            .Join(context.AlertExcursions.OfCondition(includeTest: true),
                i => i.AlertExcursionId,
                e => e.Id,
                (i, e) => new { Instance = i, Excursion = e })
            .Join(context.AlertRules,
                x => x.Excursion.AlertRuleId,
                r => r.Id,
                (x, r) => new SnoozedInstanceSnapshot(
                    x.Instance.Id, x.Instance.TenantId, x.Instance.AlertExcursionId,
                    x.Instance.Status, x.Instance.SnoozeCount,
                    r.Id, AlertRuleMapper.Readable(r.ConditionType), r.ConditionParams, r.ClientConfiguration))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Returns a snapshot of every active excursion for the tenant, keyed by alert rule id.
    /// Materialises the projection in memory rather than via EF's expression tree so the
    /// <see cref="ActiveAlertSnapshot"/> record constructor can be used directly.
    /// </summary>
    /// <param name="ct">The cancellation token.</param>
    public virtual async Task<IReadOnlyDictionary<Guid, ActiveAlertSnapshot>> GetActiveAlertSnapshotsAsync(
        CancellationToken ct)
    {
        await using var context = await _contexts.CreateAsync(ct);

        var rows = await context.AlertExcursions
            .AsNoTracking()
            .OfCondition(includeTest: true)
            .Where(e => e.EndedAt == null)
            .Select(e => new { e.AlertRuleId, e.StartedAt, e.AcknowledgedAt })
            .ToListAsync(ct);

        var dict = new Dictionary<Guid, ActiveAlertSnapshot>(rows.Count);
        foreach (var row in rows)
        {
            // If the same rule has multiple active excursions, keep the earliest — matches the
            // semantics of "the alert is firing" rather than "the latest excursion fires".
            if (!dict.TryGetValue(row.AlertRuleId, out var existing) || row.StartedAt < existing.TriggeredAt)
            {
                dict[row.AlertRuleId] = new ActiveAlertSnapshot("firing", row.StartedAt, row.AcknowledgedAt);
            }
        }

        return dict;
    }
}
