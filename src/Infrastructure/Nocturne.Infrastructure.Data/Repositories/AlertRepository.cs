using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// Repository for alert orchestration queries and mutations.
/// Methods are virtual to allow mocking with CallBase in tests.
/// </summary>
public class AlertRepository : IAlertRepository
{
    private readonly IDbContextFactory<NocturneDbContext> _contextFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="AlertRepository"/> class.
    /// </summary>
    /// <param name="contextFactory">The database context factory.</param>
    public AlertRepository(IDbContextFactory<NocturneDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    /// <summary>
    /// Gets enabled alert rules for a specific tenant.
    /// </summary>
    /// <param name="tenantId">The unique identifier of the tenant.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of enabled alert rule snapshots.</returns>
    public virtual async Task<IReadOnlyList<AlertRuleSnapshot>> GetEnabledRulesAsync(
        Guid tenantId, CancellationToken ct)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        return await context.AlertRules
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.IsEnabled)
            .OrderBy(r => r.SortOrder)
            .Select(r => new AlertRuleSnapshot(
                r.Id, r.TenantId, r.Name, r.ConditionType,
                r.ConditionParams, r.Severity, r.ClientConfiguration, r.SortOrder,
                r.AutoResolveEnabled, r.AutoResolveParams))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Gets the alert schedules associated with a specific rule.
    /// </summary>
    /// <param name="ruleId">The unique identifier of the alert rule.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of alert schedule snapshots.</returns>
    public virtual async Task<IReadOnlyList<AlertScheduleSnapshot>> GetSchedulesForRuleAsync(
        Guid ruleId, CancellationToken ct)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        return await context.AlertSchedules
            .AsNoTracking()
            .Where(s => s.AlertRuleId == ruleId)
            .Select(s => new AlertScheduleSnapshot(
                s.Id, s.AlertRuleId, s.Name, s.IsDefault,
                s.DaysOfWeek, s.StartTime, s.EndTime, s.Timezone))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Gets the escalation steps for a specific alert schedule.
    /// </summary>
    /// <param name="scheduleId">The unique identifier of the alert schedule.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of escalation step snapshots.</returns>
    public virtual async Task<IReadOnlyList<AlertEscalationStepSnapshot>> GetEscalationStepsAsync(
        Guid scheduleId, CancellationToken ct)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        return await context.AlertEscalationSteps
            .AsNoTracking()
            .Where(e => e.AlertScheduleId == scheduleId)
            .OrderBy(e => e.StepOrder)
            .Select(e => new AlertEscalationStepSnapshot(
                e.Id, e.AlertScheduleId, e.StepOrder, e.DelaySeconds))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Creates a new alert instance record.
    /// </summary>
    /// <param name="request">The request containing alert instance details.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A snapshot of the created alert instance.</returns>
    public virtual async Task<AlertInstanceSnapshot> CreateInstanceAsync(
        CreateAlertInstanceRequest request, CancellationToken ct)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        var entity = new AlertInstanceEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = request.TenantId,
            AlertExcursionId = request.ExcursionId,
            AlertScheduleId = request.ScheduleId,
            CurrentStepOrder = request.InitialStepOrder,
            Status = request.Status,
            TriggeredAt = request.TriggeredAt,
            NextEscalationAt = request.NextEscalationAt,
        };

        context.AlertInstances.Add(entity);
        await context.SaveChangesAsync(ct);

        return new AlertInstanceSnapshot(
            entity.Id, entity.TenantId, entity.AlertExcursionId, entity.AlertScheduleId,
            entity.CurrentStepOrder, entity.Status, entity.TriggeredAt,
            entity.NextEscalationAt, entity.SnoozedUntil, entity.SnoozeCount);
    }

    /// <summary>
    /// Gets alert instances that are currently escalating and due for processing.
    /// </summary>
    /// <param name="asOf">The reference timestamp for determining if an escalation is due.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of alert instances due for escalation.</returns>
    public virtual async Task<IReadOnlyList<AlertInstanceSnapshot>> GetEscalatingInstancesDueAsync(
        DateTime asOf, CancellationToken ct)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        return await context.AlertInstances
            .AsNoTracking()
            .Where(i => i.Status == "escalating"
                        && i.NextEscalationAt != null
                        && i.NextEscalationAt <= asOf
                        && i.SnoozedUntil == null)
            .Select(i => new AlertInstanceSnapshot(
                i.Id, i.TenantId, i.AlertExcursionId, i.AlertScheduleId,
                i.CurrentStepOrder, i.Status, i.TriggeredAt,
                i.NextEscalationAt, i.SnoozedUntil, i.SnoozeCount))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Gets all alert instances associated with a specific alert excursion.
    /// </summary>
    /// <param name="excursionId">The unique identifier of the alert excursion.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of alert instance snapshots.</returns>
    public virtual async Task<IReadOnlyList<AlertInstanceSnapshot>> GetInstancesForExcursionAsync(
        Guid excursionId, CancellationToken ct)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        return await context.AlertInstances
            .AsNoTracking()
            .Where(i => i.AlertExcursionId == excursionId)
            .Select(i => new AlertInstanceSnapshot(
                i.Id, i.TenantId, i.AlertExcursionId, i.AlertScheduleId,
                i.CurrentStepOrder, i.Status, i.TriggeredAt,
                i.NextEscalationAt, i.SnoozedUntil, i.SnoozeCount))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Marks all active alert instances for a specific excursion as resolved.
    /// </summary>
    /// <param name="excursionId">The unique identifier of the alert excursion.</param>
    /// <param name="resolvedAt">The timestamp when resolution occurred.</param>
    /// <param name="ct">The cancellation token.</param>
    public virtual async Task ResolveInstancesForExcursionAsync(
        Guid excursionId, DateTime resolvedAt, CancellationToken ct)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        await context.AlertInstances
            .Where(i => i.AlertExcursionId == excursionId
                        && i.Status != "resolved")
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.Status, "resolved")
                .SetProperty(i => i.ResolvedAt, resolvedAt), ct);
    }

    /// <summary>
    /// Updates the state of an existing alert instance.
    /// </summary>
    /// <param name="request">The update request containing modified properties.</param>
    /// <param name="ct">The cancellation token.</param>
    public virtual async Task UpdateInstanceAsync(
        UpdateAlertInstanceRequest request, CancellationToken ct)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        var entity = await context.AlertInstances
            .FirstOrDefaultAsync(i => i.Id == request.Id, ct);

        if (entity == null) return;

        if (request.CurrentStepOrder.HasValue)
            entity.CurrentStepOrder = request.CurrentStepOrder.Value;

        if (request.Status is not null)
            entity.Status = request.Status;

        if (request.NextEscalationAt.HasValue)
            entity.NextEscalationAt = request.NextEscalationAt == DateTime.MinValue
                ? null : request.NextEscalationAt.Value;

        if (request.SnoozedUntil.HasValue)
            entity.SnoozedUntil = request.SnoozedUntil == DateTime.MinValue
                ? null : request.SnoozedUntil.Value;

        if (request.SnoozeCount.HasValue)
            entity.SnoozeCount = request.SnoozeCount.Value;

        await context.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Marks pending deliveries as expired for a set of alert instances.
    /// </summary>
    /// <param name="instanceIds">The unique identifiers of the alert instances.</param>
    /// <param name="ct">The cancellation token.</param>
    public virtual async Task ExpirePendingDeliveriesAsync(
        IReadOnlyList<Guid> instanceIds, CancellationToken ct)
    {
        if (instanceIds.Count == 0) return;

        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        await context.AlertDeliveries
            .Where(d => instanceIds.Contains(d.AlertInstanceId)
                        && d.Status == "pending")
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status, "expired"), ct);
    }

    /// <summary>
    /// Counts the number of active alert excursions for a specific tenant.
    /// </summary>
    /// <param name="tenantId">The unique identifier of the tenant.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The count of active excursions.</returns>
    public virtual async Task<int> CountActiveExcursionsAsync(
        Guid tenantId, CancellationToken ct)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        return await context.AlertExcursions
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.EndedAt == null)
            .CountAsync(ct);
    }

    /// <summary>
    /// Gets alert excursions that are currently in the hysteresis (recovery) period.
    /// </summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of hysteresis excursion snapshots.</returns>
    public virtual async Task<IReadOnlyList<HysteresisExcursionSnapshot>> GetExcursionsInHysteresisAsync(
        CancellationToken ct)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        return await context.AlertExcursions
            .AsNoTracking()
            .Where(e => e.HysteresisStartedAt != null && e.EndedAt == null)
            .Select(e => new HysteresisExcursionSnapshot(
                e.Id, e.AlertRuleId, e.HysteresisStartedAt))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Closes an alert excursion that has completed its hysteresis period.
    /// </summary>
    /// <param name="excursionId">The unique identifier of the alert excursion.</param>
    /// <param name="alertRuleId">The unique identifier of the associated alert rule.</param>
    /// <param name="endedAt">The timestamp when the excursion ended.</param>
    /// <param name="ct">The cancellation token.</param>
    public virtual async Task CloseHysteresisExcursionAsync(
        Guid excursionId, Guid alertRuleId, DateTime endedAt, CancellationToken ct)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        // Close the excursion
        var excursion = await context.AlertExcursions
            .FirstOrDefaultAsync(e => e.Id == excursionId, ct);

        if (excursion != null)
        {
            excursion.EndedAt = endedAt;
        }

        // Resolve any remaining instances for this excursion
        await context.AlertInstances
            .Where(i => i.AlertExcursionId == excursionId && i.Status != "resolved")
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.Status, "resolved")
                .SetProperty(i => i.ResolvedAt, endedAt), ct);

        // Reset tracker state for the rule to idle
        var tracker = await context.AlertTrackerState
            .FirstOrDefaultAsync(t => t.AlertRuleId == alertRuleId, ct);

        if (tracker != null)
        {
            tracker.State = "idle";
            tracker.ConfirmationCount = 0;
            tracker.ActiveExcursionId = null;
            tracker.UpdatedAt = endedAt;
        }

        await context.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Gets basic context for a tenant's alert processing.
    /// </summary>
    /// <param name="tenantId">The unique identifier of the tenant.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The tenant alert context, or null if not found.</returns>
    public virtual async Task<TenantAlertContext?> GetTenantAlertContextAsync(
        Guid tenantId, CancellationToken ct)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        return await context.Tenants
            .AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => new TenantAlertContext(
                t.Id, t.SubjectName ?? string.Empty, t.Slug, t.DisplayName,
                t.IsActive, t.LastReadingAt))
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Gets all enabled rules for signal loss detection.
    /// </summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of signal loss rule snapshots.</returns>
    public virtual async Task<IReadOnlyList<SignalLossRuleSnapshot>> GetEnabledSignalLossRulesAsync(
        CancellationToken ct)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        return await context.AlertRules
            .AsNoTracking()
            .Where(r => r.IsEnabled && r.ConditionType == AlertConditionType.SignalLoss)
            .Select(r => new SignalLossRuleSnapshot(r.Id, r.TenantId, r.ConditionParams))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Gets the most recent glucose trend rate for a specific tenant.
    /// </summary>
    /// <param name="tenantId">The unique identifier of the tenant.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The trend rate, or null if no readings exist.</returns>
    public virtual async Task<double?> GetLatestTrendRateAsync(
        Guid tenantId, CancellationToken ct)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        return await context.SensorGlucose
            .AsNoTracking()
            .Where(sg => sg.TenantId == tenantId)
            .OrderByDescending(sg => sg.Timestamp)
            .Select(sg => sg.TrendRate)
            .FirstOrDefaultAsync(ct);
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
        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        return await context.AlertInstances
            .AsNoTracking()
            .Where(i => i.SnoozedUntil != null
                        && i.SnoozedUntil <= asOf
                        && i.Status != "resolved")
            .Join(context.AlertExcursions,
                i => i.AlertExcursionId,
                e => e.Id,
                (i, e) => new { Instance = i, Excursion = e })
            .Join(context.AlertRules,
                x => x.Excursion.AlertRuleId,
                r => r.Id,
                (x, r) => new SnoozedInstanceSnapshot(
                    x.Instance.Id, x.Instance.TenantId, x.Instance.AlertExcursionId,
                    x.Instance.AlertScheduleId, x.Instance.CurrentStepOrder,
                    x.Instance.Status, x.Instance.SnoozeCount,
                    r.Id, r.ConditionType, r.ConditionParams, r.ClientConfiguration))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Saves all changes made in this repository to the database.
    /// </summary>
    /// <param name="ct">The cancellation token.</param>
    public virtual async Task SaveChangesAsync(CancellationToken ct)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct);
        await context.SaveChangesAsync(ct);
    }
}
