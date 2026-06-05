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
/// <remarks>
/// Every method leases a context from the pooled factory and pins <c>context.TenantId</c>
/// before touching any <c>ITenantScoped</c> table. Pooling does not reset that property, and
/// both the EF global query filter and PostgreSQL Row-Level Security derive the active tenant
/// from it — leaving it unset would scope reads/writes to whatever tenant the previous lessee
/// left on the context (or fail closed under RLS when it lands as <see cref="Guid.Empty"/>).
/// Cross-tenant sweeps cannot be expressed as a single query because the RLS policy
/// (<c>tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid</c>) admits
/// no "see all" value for the non-bypass app role, so they iterate active tenants explicitly.
/// </remarks>
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
        context.TenantId = tenantId;

        return await context.AlertRules
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.IsEnabled)
            .OrderBy(r => r.SortOrder)
            .Select(r => new AlertRuleSnapshot(
                r.Id, r.TenantId, r.Name, r.ConditionType,
                r.ConditionParams, r.Severity, r.ClientConfiguration, r.SortOrder,
                r.AutoResolveEnabled, r.AutoResolveParams, r.AllowThroughDnd))
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<AlertRuleChannelSnapshot>> GetChannelsForRuleAsync(
        Guid tenantId, Guid ruleId, CancellationToken ct)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct);
        context.TenantId = tenantId;

        return await context.AlertRuleChannels
            .AsNoTracking()
            .Where(c => c.AlertRuleId == ruleId)
            .OrderBy(c => c.SortOrder)
            .Select(c => new AlertRuleChannelSnapshot(
                c.Id, c.AlertRuleId, c.ChannelType,
                c.Destination, c.DestinationLabel, c.SortOrder, c.Metadata))
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public virtual async Task<AlertInstanceSnapshot> CreateInstanceAsync(
        CreateAlertInstanceRequest request, CancellationToken ct)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct);
        context.TenantId = request.TenantId;

        var entity = new AlertInstanceEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = request.TenantId,
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
        Guid tenantId, Guid excursionId, CancellationToken ct)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct);
        context.TenantId = tenantId;

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
    public virtual async Task ResolveInstancesForExcursionAsync(
        Guid tenantId, Guid excursionId, DateTime resolvedAt, string? resolutionReason, CancellationToken ct)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct);
        context.TenantId = tenantId;

        await context.AlertInstances
            .Where(i => i.AlertExcursionId == excursionId
                        && i.Status != "resolved")
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.Status, "resolved")
                .SetProperty(i => i.ResolvedAt, resolvedAt)
                .SetProperty(i => i.ResolutionReason, resolutionReason), ct);
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
        context.TenantId = request.TenantId;

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

    /// <summary>
    /// Marks pending deliveries as expired for a set of alert instances.
    /// </summary>
    /// <param name="tenantId">The unique identifier of the tenant.</param>
    /// <param name="instanceIds">The unique identifiers of the alert instances.</param>
    /// <param name="ct">The cancellation token.</param>
    public virtual async Task ExpirePendingDeliveriesAsync(
        Guid tenantId, IReadOnlyList<Guid> instanceIds, CancellationToken ct)
    {
        if (instanceIds.Count == 0) return;

        await using var context = await _contextFactory.CreateDbContextAsync(ct);
        context.TenantId = tenantId;

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
        context.TenantId = tenantId;

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
        // Cross-tenant scan — sweep evaluates every tenant in a single tick, then sets
        // tenant context per-excursion before invoking the tracker. RLS scopes each query to
        // one tenant, so iterate active tenants rather than relying on IgnoreQueryFilters
        // (which bypasses the EF filter but not the fail-closed RLS policy).
        var results = new List<HysteresisExcursionSnapshot>();
        foreach (var tenantId in await GetActiveTenantIdsAsync(ct))
        {
            await using var context = await _contextFactory.CreateDbContextAsync(ct);
            context.TenantId = tenantId;

            var rows = await context.AlertExcursions
                .AsNoTracking()
                .Where(e => e.HysteresisStartedAt != null && e.EndedAt == null)
                .Select(e => new HysteresisExcursionSnapshot(
                    e.Id, e.TenantId, e.AlertRuleId, e.HysteresisStartedAt))
                .ToListAsync(ct);
            results.AddRange(rows);
        }

        return results;
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<string>> GetInAppDestinationsForExcursionAsync(
        Guid tenantId, Guid excursionId, CancellationToken ct)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct);
        context.TenantId = tenantId;

        return await context.AlertDeliveries
            .AsNoTracking()
            .Where(d => d.ChannelType == ChannelType.InApp)
            .Join(context.AlertInstances.AsNoTracking(),
                d => d.AlertInstanceId, i => i.Id, (d, i) => new { d, i })
            .Where(x => x.i.AlertExcursionId == excursionId
                        && !string.IsNullOrEmpty(x.d.Destination))
            .Select(x => x.d.Destination)
            .Distinct()
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<AutoResolveExcursionSnapshot>> GetAutoResolveExcursionsAsync(
        CancellationToken ct)
    {
        // Cross-tenant scan: the sweep evaluates every tenant's auto-resolvable excursions in a
        // single tick. RLS scopes each query to one tenant, so iterate active tenants.
        var results = new List<AutoResolveExcursionSnapshot>();
        foreach (var tenantId in await GetActiveTenantIdsAsync(ct))
        {
            await using var context = await _contextFactory.CreateDbContextAsync(ct);
            context.TenantId = tenantId;

            var rows = await context.AlertExcursions
                .AsNoTracking()
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
                        x.r.AutoResolveEnabled, x.r.AutoResolveParams, x.r.AllowThroughDnd)))
                .ToListAsync(ct);
            results.AddRange(rows);
        }

        return results;
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
        // patient_records is RLS-protected; stamp the tenant context before any scoped read so
        // the interceptor sets app.current_tenant_id. tenants is not tenant-scoped, so it is
        // readable regardless, but pinning up front keeps the whole method consistent.
        context.TenantId = tenantId;

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
        Guid tenantId, Guid instanceId, string reason, CancellationToken ct)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct);
        context.TenantId = tenantId;

        var instance = await context.AlertInstances
            .FirstOrDefaultAsync(i => i.Id == instanceId && i.TenantId == tenantId, ct);
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

    /// <inheritdoc/>
    public virtual async Task<TenantAlertSettingsSnapshot> GetTenantAlertSettingsAsync(
        Guid tenantId, CancellationToken ct)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct);
        context.TenantId = tenantId;

        var row = await context.TenantAlertSettings
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId)
            .Select(s => new TenantAlertSettingsSnapshot(
                s.DndManualActive,
                s.DndManualUntil,
                s.DndManualStartedAt,
                s.DndScheduleEnabled,
                s.DndScheduleStart,
                s.DndScheduleEnd))
            .FirstOrDefaultAsync(ct);

        return row ?? TenantAlertSettingsSnapshot.Empty;
    }

    /// <summary>
    /// Gets all enabled rules for signal loss detection.
    /// </summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of signal loss rule snapshots.</returns>
    public virtual async Task<IReadOnlyList<SignalLossRuleSnapshot>> GetEnabledSignalLossRulesAsync(
        CancellationToken ct)
    {
        // Cross-tenant scan: iterate active tenants so RLS scopes each query correctly.
        var results = new List<SignalLossRuleSnapshot>();
        foreach (var tenantId in await GetActiveTenantIdsAsync(ct))
        {
            await using var context = await _contextFactory.CreateDbContextAsync(ct);
            context.TenantId = tenantId;

            var rows = await context.AlertRules
                .AsNoTracking()
                .Where(r => r.IsEnabled && r.ConditionType == AlertConditionType.SignalLoss)
                .Select(r => new SignalLossRuleSnapshot(r.Id, r.TenantId, r.ConditionParams))
                .ToListAsync(ct);
            results.AddRange(rows);
        }

        return results;
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
        context.TenantId = tenantId;

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
        // Cross-tenant scan: iterate active tenants so RLS scopes each query correctly.
        var results = new List<SnoozedInstanceSnapshot>();
        foreach (var tenantId in await GetActiveTenantIdsAsync(ct))
        {
            await using var context = await _contextFactory.CreateDbContextAsync(ct);
            context.TenantId = tenantId;

            var rows = await context.AlertInstances
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
                        x.Instance.Status, x.Instance.SnoozeCount,
                        r.Id, r.ConditionType, r.ConditionParams, r.ClientConfiguration))
                .ToListAsync(ct);
            results.AddRange(rows);
        }

        return results;
    }

    /// <summary>
    /// Returns a snapshot of every active excursion for the tenant, keyed by alert rule id.
    /// Materialises the projection in memory rather than via EF's expression tree so the
    /// <see cref="ActiveAlertSnapshot"/> record constructor can be used directly.
    /// </summary>
    /// <param name="tenantId">The unique identifier of the tenant.</param>
    /// <param name="ct">The cancellation token.</param>
    public virtual async Task<IReadOnlyDictionary<Guid, ActiveAlertSnapshot>> GetActiveAlertSnapshotsAsync(
        Guid tenantId, CancellationToken ct)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct);
        context.TenantId = tenantId;

        var rows = await context.AlertExcursions
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.EndedAt == null)
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

    /// <summary>
    /// Saves all changes made in this repository to the database.
    /// </summary>
    /// <param name="ct">The cancellation token.</param>
    public virtual async Task SaveChangesAsync(CancellationToken ct)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct);
        await context.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Lists the ids of every active tenant. Reads the <c>tenants</c> table, which is not
    /// tenant-scoped (no RLS policy), so it is queryable without a pinned tenant context and
    /// drives the per-tenant iteration of the cross-tenant sweeps.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> GetActiveTenantIdsAsync(CancellationToken ct)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct);
        return await context.Tenants
            .AsNoTracking()
            .Where(t => t.IsActive)
            .Select(t => t.Id)
            .ToListAsync(ct);
    }
}
