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
    public virtual async Task<IReadOnlyList<AlertRuleSnapshot>> GetEnabledRulesAsync(CancellationToken ct)
    {
        await using var context = await _contexts.CreateAsync(ct);

        return (await ReadEnabledRulesAsync(context, ct)).Rules;
    }

    /// <inheritdoc/>
    public virtual async Task<EnabledAlertRules> ReadEnabledRulesAsync(Guid tenantId, CancellationToken ct)
    {
        await using var context = await _contexts.CreateForTenantAsync(tenantId, ct);

        return await ReadEnabledRulesAsync(context, ct);
    }

    /// <summary>
    /// The context's tenant's enabled rules, less any whose condition type or severity no enum
    /// member has.
    /// </summary>
    /// <remarks>
    /// The enum converter throws on such a value, which fails the whole read, so one stale row
    /// would stop every rule of the tenant evaluating. Each skipped rule is logged as an error
    /// instead, every time it is skipped.
    /// </remarks>
    private async Task<EnabledAlertRules> ReadEnabledRulesAsync(NocturneDbContext context, CancellationToken ct)
    {
        try
        {
            return new EnabledAlertRules(await EnabledRules(context, []).ToListAsync(ct), []);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            var tenantId = context.TenantId;
            var unreadable = await context.Database
                .SqlQuery<StoredRuleEnums>(
                    $"""SELECT id AS "Id", condition_type AS "ConditionType", severity AS "Severity" FROM alert_rules WHERE tenant_id = {tenantId} AND is_enabled""")
                .ToListAsync(ct);
            unreadable.RemoveAll(r =>
                EnumMemberValueConverter<AlertConditionType>.CanRead(r.ConditionType)
                && EnumMemberValueConverter<AlertRuleSeverity>.CanRead(r.Severity));
            if (unreadable.Count == 0)
                throw;

            foreach (var rule in unreadable)
            {
                _logger.LogError(
                    "Alert rule {AlertRuleId} for tenant {TenantId} is not evaluated: condition type '{ConditionType}' or severity '{Severity}' is not one the engine knows",
                    rule.Id, tenantId, rule.ConditionType, rule.Severity);
            }

            var excluded = unreadable.Select(r => r.Id).ToList();
            return new EnabledAlertRules(await EnabledRules(context, excluded).ToListAsync(ct), excluded);
        }
    }

    private static IQueryable<AlertRuleSnapshot> EnabledRules(NocturneDbContext context, List<Guid> excluded) =>
        Snapshots(context.AlertRules
            .AsNoTracking()
            .Where(r => r.IsEnabled && !excluded.Contains(r.Id))
            .OrderBy(r => r.SortOrder));

    private static IQueryable<AlertRuleSnapshot> Snapshots(IQueryable<AlertRuleEntity> rules) =>
        rules.Select(r => new AlertRuleSnapshot(
            r.Id, r.TenantId, r.Name, r.ConditionType,
            r.ConditionParams, r.Severity, r.ClientConfiguration, r.SortOrder,
            r.AutoResolveEnabled, r.AutoResolveParams, r.AllowThroughDnd, r.ScopeClass, r.OnUnknown,
            r.GlucoseFreshnessMinutes, r.EnabledAt));

    private sealed class StoredRuleEnums
    {
        public Guid Id { get; init; }
        public string ConditionType { get; init; } = string.Empty;
        public string Severity { get; init; } = string.Empty;
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

        return await Snapshots(context.AlertRules
                .AsNoTracking()
                .OrderBy(r => r.SortOrder))
            .ToListAsync(ct);
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

    /// <inheritdoc/>
    public virtual async Task<AlertInstanceSnapshot> CreateInstanceAsync(
        CreateAlertInstanceRequest request, CancellationToken ct)
    {
        await using var context = await _contexts.CreateAsync(ct);

        var entity = new AlertInstanceEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = context.TenantId,
            AlertExcursionId = request.ExcursionId,
            Status = request.Status,
            TriggeredAt = request.TriggeredAt,
        };

        context.AlertInstances.Add(entity);
        await context.SaveChangesAsync(ct);

        return new AlertInstanceSnapshot(
            entity.Id, entity.TenantId, entity.AlertExcursionId,
            entity.Status, entity.TriggeredAt,
            entity.SnoozedUntil, entity.SnoozeCount);
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
                (e, r) => new ExcursionRuleSnapshot(r.Id, r.Name, e.UnevaluableConditionVersion != null))
            .FirstOrDefaultAsync(ct);
    }

    /// <inheritdoc/>
    public virtual async Task ResolveInstancesForExcursionAsync(
        Guid excursionId, DateTime resolvedAt, string? resolutionReason, CancellationToken ct)
    {
        await using var context = await _contexts.CreateAsync(ct);

        await context.AlertInstances
            .Where(i => i.AlertExcursionId == excursionId
                        && i.Status != "resolved")
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.Status, "resolved")
                .SetProperty(i => i.ResolvedAt, resolvedAt)
                .SetProperty(i => i.ResolutionReason, resolutionReason), ct);
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

        return await context.AlertExcursions
            .AsNoTracking()
            .OfCondition(includeTest: true)
            .Where(e => e.EndedAt == null)
            .Join(context.AlertRules.AsNoTracking(),
                e => e.AlertRuleId, r => r.Id, (e, r) => new { e, r })
            .Where(x => x.r.IsEnabled
                        && x.r.AutoResolveEnabled
                        && x.r.AutoResolveParams != null)
            .Select(x => new AutoResolveExcursionSnapshot(
                x.e.Id,
                x.e.TenantId,
                new AlertRuleSnapshot(
                    x.r.Id, x.r.TenantId, x.r.Name, x.r.ConditionType,
                    x.r.ConditionParams, x.r.Severity, x.r.ClientConfiguration, x.r.SortOrder,
                    x.r.AutoResolveEnabled, x.r.AutoResolveParams, x.r.AllowThroughDnd, x.r.ScopeClass,
                    x.r.OnUnknown, x.r.GlucoseFreshnessMinutes, x.r.EnabledAt)))
            .ToListAsync(ct);
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
            tenant.Id, preferredName ?? string.Empty, tenant.Slug, tenant.DisplayName,
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

    private const int UnevaluableNoticeLockClass = 0x4E55_4E56;

    /// <inheritdoc/>
    /// <remarks>
    /// The rule is read again under the lock: a disable commits before its retirement resolves the
    /// rule's notices, so either this sees the rule disabled and records nothing, or the retirement
    /// sees the notice this records. An edit made since the caller read the rule likewise records
    /// nothing for the condition it replaced. See <see cref="AdvisoryLockExtensions"/>.
    /// </remarks>
    public virtual async Task<UnevaluableNoticeRecorded?> RecordUnevaluableNoticeAsync(
        AlertRuleSnapshot rule, string conditionVersion, DateTime at, CancellationToken ct)
    {
        var ruleId = rule.Id;
        await using var context = await _contexts.CreateAsync(ct);
        return await context.ExecuteInTransactionAsync<UnevaluableNoticeRecorded?>(async token =>
        {
            await context.LockForTransactionAsync(UnevaluableNoticeLockClass, ruleId, token);

            if (!await context.AlertRules.AnyAsync(r => r.Id == ruleId
                                                        && r.IsEnabled
                                                        && r.ConditionType == rule.ConditionType
                                                        && r.ConditionParams == rule.ConditionParams, token))
                return null;

            var current = await Standing(context, ruleId, conditionVersion).FirstOrDefaultAsync(token);
            if (current is not null)
                return new UnevaluableNoticeRecorded(current.ExcursionId, current.InstanceId, current.Dispatched);

            var excursion = new AlertExcursionEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = context.TenantId,
                AlertRuleId = ruleId,
                StartedAt = at,
                EndedAt = AlertExcursionEntity.NoticeStandingEnd,
                UnevaluableConditionVersion = conditionVersion,
            };
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

            return new UnevaluableNoticeRecorded(excursion.Id, instance.Id, Dispatched: false);
        },
        verifySucceeded: async (recorded, token) =>
            recorded is not null
            && await context.AlertExcursions.StandingNotices().AnyAsync(e => e.Id == recorded.ExcursionId, token),
        ct: ct);
    }

    /// <inheritdoc/>
    public virtual async Task EndUnevaluableNoticeAsync(Guid excursionId, DateTime at, CancellationToken ct)
    {
        await using var context = await _contexts.CreateAsync(ct);

        var notice = await context.AlertExcursions
            .StandingNotices()
            .FirstOrDefaultAsync(e => e.Id == excursionId && e.EndedAt > at, ct);
        if (notice is null) return;

        notice.EndedAt = at;
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
            .Where(i => ruleId == null || i.AlertExcursion!.AlertRuleId == ruleId)
            .Where(i => conditionVersion == null || i.AlertExcursion!.UnevaluableConditionVersion == conditionVersion)
            .Select(i => new StandingUnevaluableNotice(
                i.AlertExcursionId,
                i.Id,
                i.AlertExcursion!.AlertRuleId,
                i.AlertExcursion.UnevaluableConditionVersion!,
                i.SuppressionReason != null || context.AlertDeliveries.Any(d => d.AlertInstanceId == i.Id)));

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
                    r.Id, r.ConditionType, r.ConditionParams, r.ClientConfiguration))
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
