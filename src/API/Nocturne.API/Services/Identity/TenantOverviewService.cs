using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.Glucose;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services.Identity;

/// <summary>
/// Cross-tenant caregiver overview. Enumerates the subject's active memberships from a
/// subject-pinned factory context (same pattern as <see cref="TenantService.GetTenantsForSubjectAsync"/>),
/// then fans out per tenant: a fresh DI scope pinned via <see cref="ITenantAccessor"/> for the
/// canonical glucose read, and a context from <see cref="INocturneDbContextFactory.CreateForTenantAsync"/>
/// for alert rules and active excursions. Never reads as the request's tenant, so the endpoint
/// works from the apex (tenantless) in multi-tenant deployments.
/// </summary>
/// <seealso cref="ITenantOverviewService"/>
public class TenantOverviewService : ITenantOverviewService
{
    private const string AuditEndpoint = "service:tenant-overview";

    private readonly INocturneDbContextFactory _factory;
    private readonly IServiceProvider _scope;
    private readonly IGlucoseStatusClassifier _classifier;
    private readonly ILogger<TenantOverviewService> _logger;

    public TenantOverviewService(
        INocturneDbContextFactory factory,
        IServiceProvider scope,
        IGlucoseStatusClassifier classifier,
        ILogger<TenantOverviewService> logger)
    {
        _factory = factory;
        _scope = scope;
        _classifier = classifier;
        _logger = logger;
    }

    public async Task<TenantOverviewResponse> GetOverviewAsync(
        Guid subjectId, IReadOnlySet<string> tokenScopes, AuthType authType,
        bool credentialLimitTo24Hours = false,
        CancellationToken ct = default)
    {
        var glucoseReadTenants = await GetGlucoseReadTenantsAsync(subjectId, tokenScopes, authType, ct);

        var items = new List<TenantOverviewItem>();
        foreach (var (tenant, allowed, membershipLimitTo24Hours, effectivePermissions) in glucoseReadTenants)
        {
            var includeAlerts = Scope.Satisfies(allowed, Scope.AlertsRead);
            var historyClamped = MemberScopeResolver.IsHistoryClamped(
                credentialLimitTo24Hours, membershipLimitTo24Hours, effectivePermissions);

            try
            {
                items.Add(await BuildItemAsync(tenant, includeAlerts, historyClamped, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to build overview for tenant {TenantId}", tenant.Id);
                items.Add(new TenantOverviewItem(
                    tenant.Id, tenant.Slug, tenant.DisplayName,
                    historyClamped ? null : tenant.LastReadingAt, Latest: null, GlucoseStatus.Unknown,
                    _classifier.Defaults, ActiveAlertCount: null, HighestActiveSeverity: null));
            }
        }

        return new TenantOverviewResponse(items);
    }

    public async Task<IReadOnlyList<GlucoseReadTenant>> GetGlucoseReadTenantsAsync(
        Guid subjectId, IReadOnlySet<string> tokenScopes, AuthType authType,
        CancellationToken ct = default)
    {
        // The read spans tenants for one person, so the context is pinned to the subject rather
        // than to a tenant.
        await using var context = await _factory.CreateForSubjectAsync(subjectId, ct);
        var memberships = await context.TenantMembers.AsNoTracking()
            .Include(tm => tm.Tenant)
            .Include(tm => tm.MemberRoles).ThenInclude(mr => mr.TenantRole)
            .Where(tm => tm.SubjectId == subjectId)
            .ToListAsync(ct);

        var result = new List<GlucoseReadTenant>();
        foreach (var membership in memberships)
        {
            var tenant = membership.Tenant;
            if (tenant is null || !tenant.IsActive) continue;

            var effectivePermissions = membership.EffectivePermissions().ToHashSet();
            var allowed = MemberScopeResolver.Resolve(effectivePermissions, authType, tokenScopes);
            if (!Scope.Satisfies(allowed, Scope.GlucoseRead)) continue;

            result.Add(new GlucoseReadTenant(
                tenant, allowed, membership.LimitTo24Hours, effectivePermissions));
        }

        return result;
    }

    private async Task<TenantOverviewItem> BuildItemAsync(
        TenantEntity tenant,
        bool includeAlerts,
        bool historyClamped,
        CancellationToken ct)
    {
        // A scope per tenant, since CanonicalGlucoseService caches per scope. The caller's request
        // never passed through MemberScopeMiddleware for this tenant, so its clamp here is the one
        // this membership decides, and every read below goes through the scope.
        var reader = TenantReader.Of(_scope)
            .ForTenant(new TenantContext(tenant.Id, tenant.Slug, tenant.DisplayName, true, tenant.IsDemo))
            .ClampedToDay(historyClamped);
        await using var scope = TenantJobScope.OpenFor(_scope, reader, AuditEndpoint);
        var latest = await scope.ServiceProvider
            .GetRequiredService<ICanonicalGlucoseService>()
            .GetLatestAsync(ct);

        await using var db = await scope.ServiceProvider
            .GetRequiredService<INocturneDbContextFactory>()
            .CreateAsync(ct);

        int? activeAlertCount = null;
        AlertRuleSeverity? highestSeverity = null;
        if (includeAlerts)
        {
            var activeExcursions = await db.AlertExcursions.AsNoTracking()
                .Include(e => e.AlertRule)
                .Where(e => e.EndedAt == null)
                .ToListAsync(ct);

            activeAlertCount = activeExcursions.Count;
            // AlertRuleSeverity orders Critical=0 < Warning < Info, so Min is most severe.
            highestSeverity = activeExcursions
                .Where(e => e.AlertRule is not null)
                .Select(e => (AlertRuleSeverity?)e.AlertRule!.Severity)
                .Min();
        }

        var thresholds = await _classifier.ResolveThresholdsAsync(db, ct);

        var reading = latest is null
            ? null
            : new TenantOverviewReading(latest.Mgdl, latest.Delta, latest.Direction, latest.TrendRate, latest.Timestamp);

        // tenant.LastReadingAt is denormalised from glucose rows RLS never sees, so a clamped
        // caller must not read an older reading's time off it.
        var lastReadingAt = historyClamped ? null : tenant.LastReadingAt;

        var status = _classifier.Classify(
            latest?.Mgdl, latest?.Timestamp, lastReadingAt, thresholds, DateTime.UtcNow);

        return new TenantOverviewItem(
            tenant.Id, tenant.Slug, tenant.DisplayName,
            latest?.Timestamp ?? lastReadingAt,
            reading, status, thresholds,
            activeAlertCount, highestSeverity);
    }
}
