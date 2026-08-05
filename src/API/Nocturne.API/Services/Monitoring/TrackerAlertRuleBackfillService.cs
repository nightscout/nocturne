using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Services.Monitoring;

/// <summary>
/// One-time startup backfill that synthesises managed alert rules for tracker notification
/// thresholds created before the tracker→alert-rule unification (or whose rule was deleted:
/// the FK is SET NULL, so a lost rule shows up here as an unlinked threshold and heals).
/// After this lands, the trackers controller syncs on every definition create/update, so the
/// steady state is a no-op.
/// </summary>
/// <remarks>
/// Idempotent and safe to run on every startup: only definitions with at least one threshold
/// lacking an <c>alert_rule_id</c> are synced. Runs as a <see cref="BackgroundService"/> so
/// the scan never gates host startup. The scan is per-tenant because both tables are
/// RLS-scoped with fail-closed policies — the tenant context is set per iteration exactly
/// like the sweep service does.
/// </remarks>
/// <seealso cref="TrackerAlertRuleSyncService"/>
public sealed class TrackerAlertRuleBackfillService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<TrackerAlertRuleBackfillService> _logger;

    /// <summary>Initialises a new <see cref="TrackerAlertRuleBackfillService"/>.</summary>
    public TrackerAlertRuleBackfillService(
        IServiceProvider serviceProvider,
        ILogger<TrackerAlertRuleBackfillService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Detach from the sequential hosted-service startup chain before touching anything.
        await Task.Yield();

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<NocturneDbContext>>();

            // Tenants are not RLS-scoped, so this list read is safe without a tenant context.
            List<(Guid TenantId, string? Slug, string? DisplayName)> tenants;
            await using (var lookup = await factory.CreateDbContextAsync(stoppingToken))
            {
                tenants = (await lookup.Tenants
                    .AsNoTracking()
                    .Where(t => t.IsActive)
                    .Select(t => new { t.Id, t.Slug, t.DisplayName })
                    .ToListAsync(stoppingToken))
                    .Select(t => (t.Id, (string?)t.Slug, (string?)t.DisplayName))
                    .ToList();
            }

            var syncedTotal = 0;
            foreach (var (tenantId, slug, displayName) in tenants)
            {
                List<Guid> definitionIds;
                await using (var db = await factory.CreateDbContextAsync(stoppingToken))
                {
                    db.TenantId = tenantId;
                    definitionIds = await db.TrackerNotificationThresholds
                        .AsNoTracking()
                        .Where(t => t.AlertRuleId == null)
                        .Select(t => t.TrackerDefinitionId)
                        .Distinct()
                        .ToListAsync(stoppingToken);
                }

                if (definitionIds.Count == 0) continue;

                // The sync service resolves its db context through the ambient tenant
                // accessor, so give each tenant its own scope with the tenant set.
                using var tenantScope = _serviceProvider.CreateScope();
                var tenantAccessor = tenantScope.ServiceProvider.GetRequiredService<ITenantAccessor>();
                tenantAccessor.SetTenant(new TenantContext(
                    tenantId, slug ?? string.Empty, displayName ?? string.Empty, true, IsDemo: false));
                var sync = tenantScope.ServiceProvider.GetRequiredService<ITrackerAlertRuleSyncService>();

                foreach (var definitionId in definitionIds)
                {
                    try
                    {
                        await sync.SyncDefinitionAsync(definitionId, stoppingToken);
                        syncedTotal++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "Error backfilling managed alert rules for tracker definition {DefinitionId} on tenant {TenantId}",
                            definitionId, tenantId);
                    }
                }
            }

            if (syncedTotal > 0)
            {
                _logger.LogInformation(
                    "Backfilled managed alert rules for {Count} tracker definition(s)", syncedTotal);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error backfilling tracker-managed alert rules");
        }
    }
}
