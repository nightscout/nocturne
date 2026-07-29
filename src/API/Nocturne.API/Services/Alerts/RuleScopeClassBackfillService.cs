using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Alerts.Native;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// One-time startup backfill that stamps <c>scope_class</c> on every existing alert rule for
/// scoped Do Not Disturb (ADR 0004). Pre-existing rules were created before classification
/// existed and default to <c>undirected</c> (all-only) from the D2 migration; this recomputes
/// each one through <see cref="IRuleScopeClassifier"/> so scoped <c>lows</c>/<c>highs</c> windows
/// can narrow-match them. After this lands, the controller computes <c>scope_class</c> on every
/// create/update, so the steady state is a no-op.
/// </summary>
/// <remarks>
/// Idempotent and safe to run on every startup: only rows whose recomputed class differs from the
/// stored one are written, so once the population is classified the scan writes nothing. Runs as a
/// <see cref="BackgroundService"/> so the scan never gates host startup, reads only the columns
/// the classifier needs (untracked projection), and writes via targeted
/// <c>ExecuteUpdateAsync</c>. The scan is per-tenant because <c>alert_rules</c> is RLS-scoped and
/// the policy is fail-closed — a cross-tenant <c>IgnoreQueryFilters</c> read would be blocked, so
/// we set the tenant context per iteration exactly like the sweep service does.
/// </remarks>
/// <seealso cref="RuleScopeClassifier"/>
public sealed class RuleScopeClassBackfillService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<RuleScopeClassBackfillService> _logger;

    public RuleScopeClassBackfillService(
        IServiceProvider serviceProvider,
        ILogger<RuleScopeClassBackfillService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Detach from the sequential hosted-service startup chain before touching anything.
        await Task.Yield();

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<NocturneDbContext>>();
            var classifier = scope.ServiceProvider.GetRequiredService<IRuleScopeClassifier>();

            // Without the native engine every Classify falls back to Undirected, and the
            // recompute-and-compare below would overwrite previously-correct low/high
            // classifications with that fallback. Skip the whole scan instead of persisting it.
            // Asked of the classifier rather than AlertsInterop directly: it is the thing whose
            // availability actually matters here, and it caches the probe for the process lifetime.
            if (!classifier.IsAvailable)
            {
                _logger.LogWarning(
                    "nocturne_alerts native library unavailable; skipping scope-class backfill to preserve stored classifications");
                return;
            }

            // Tenants are not RLS-scoped, so this list read is safe without a tenant context.
            List<Guid> tenantIds;
            await using (var lookup = await factory.CreateDbContextAsync(stoppingToken))
            {
                tenantIds = await lookup.Tenants
                    .AsNoTracking()
                    .Where(t => t.IsActive)
                    .Select(t => t.Id)
                    .ToListAsync(stoppingToken);
            }

            var updatedTotal = 0;
            foreach (var tenantId in tenantIds)
            {
                await using var db = await factory.CreateDbContextAsync(stoppingToken);
                db.TenantId = tenantId;

                // The classifier needs only the condition columns; project instead of
                // materialising tracked entity graphs for every rule of every tenant.
                var rules = await db.AlertRules
                    .AsNoTracking()
                    .Where(r => r.TenantId == tenantId)
                    .Select(r => new { r.Id, r.ConditionType, r.ConditionParams, r.ScopeClass })
                    .ToListAsync(stoppingToken);

                // Group reclassified rules so each distinct target class is one UPDATE.
                var reclassified = rules
                    .Select(r => (r.Id, Computed: classifier.Classify(r.ConditionType, r.ConditionParams), r.ScopeClass))
                    .Where(r => r.ScopeClass != r.Computed)
                    .GroupBy(r => r.Computed, r => r.Id);

                foreach (var group in reclassified)
                {
                    var ids = group.ToList();
                    var scopeClass = group.Key;
                    updatedTotal += await db.AlertRules
                        .Where(r => ids.Contains(r.Id))
                        .ExecuteUpdateAsync(s => s.SetProperty(r => r.ScopeClass, scopeClass), stoppingToken);
                }
            }

            if (updatedTotal > 0)
            {
                _logger.LogInformation(
                    "Backfilled scope_class for {Count} alert rule(s) across {Tenants} tenant(s)",
                    updatedTotal,
                    tenantIds.Count);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error backfilling alert-rule scope classes");
        }
    }
}
