using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.Glucose;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;

namespace Nocturne.API.Services.Identity;

/// <summary>
/// Cross-tenant caregiver overview. Enumerates the subject's active memberships from a
/// subject-pinned factory context (same pattern as <see cref="TenantService.GetTenantsForSubjectAsync"/>),
/// then fans out per tenant: a fresh DI scope pinned via <see cref="ITenantAccessor"/> for the
/// canonical glucose read, and a factory context pinned via <see cref="NocturneDbContext.TenantId"/>
/// for alert rules and active excursions. Never touches the request-scoped DbContext, so the
/// endpoint works from the apex (tenantless) in multi-tenant deployments.
/// </summary>
/// <seealso cref="ITenantOverviewService"/>
public class TenantOverviewService : ITenantOverviewService
{
    private readonly IDbContextFactory<NocturneDbContext> _factory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IGlucoseStatusClassifier _classifier;
    private readonly ILogger<TenantOverviewService> _logger;

    public TenantOverviewService(
        IDbContextFactory<NocturneDbContext> factory,
        IServiceScopeFactory scopeFactory,
        IGlucoseStatusClassifier classifier,
        ILogger<TenantOverviewService> logger)
    {
        _factory = factory;
        _scopeFactory = scopeFactory;
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
        foreach (var (tenant, allowed, membershipClamped) in glucoseReadTenants)
        {
            var includeAlerts = Scope.Satisfies(allowed, Scope.AlertsRead);
            var historyClamped = membershipClamped || credentialLimitTo24Hours;

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
        // tenant_members has a global RevokedAt == null query filter, so revoked
        // memberships are already excluded here. The read spans tenants for one person, so the
        // context is pinned to the subject rather than to a tenant.
        await using var context = await _factory.CreateSubjectPinnedContextAsync(subjectId, ct);
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

            var allowed = ResolveAllowedScopes(membership, tokenScopes, authType);
            if (!Scope.Satisfies(allowed, Scope.GlucoseRead)) continue;

            var membershipClamped = membership.LimitTo24Hours
                && !MemberScopeResolver.IsExemptFromHistoryClamp(EffectivePermissions(membership));

            result.Add(new GlucoseReadTenant(tenant, allowed, membershipClamped));
        }

        return result;
    }

    /// <summary>
    /// Resolves what the caller may see on this tenant. Delegates to
    /// <see cref="MemberScopeResolver"/>, the same resolution <c>MemberScopeMiddleware</c> applies
    /// per request, so the tenant picker cannot list a tenant the endpoints behind it refuse (or
    /// hide one they would serve).
    /// </summary>
    internal static IReadOnlySet<string> ResolveAllowedScopes(
        TenantMemberEntity membership, IReadOnlySet<string> tokenScopes, AuthType authType) =>
        MemberScopeResolver.Resolve(EffectivePermissions(membership), authType, tokenScopes);

    private static HashSet<string> EffectivePermissions(TenantMemberEntity membership) =>
        membership.MemberRoles
            .SelectMany(mr => mr.TenantRole.Permissions)
            .Union(membership.DirectPermissions ?? [])
            .ToHashSet();

    private async Task<TenantOverviewItem> BuildItemAsync(
        TenantEntity tenant,
        bool includeAlerts,
        bool historyClamped,
        CancellationToken ct)
    {
        // Fresh scope per tenant: CanonicalGlucoseService caches per scope. The scope never passes
        // through MemberScopeMiddleware, so the caller's clamp on this tenant is carried in here.
        SensorGlucose? latest;
        using (var scope = _scopeFactory.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<ITenantAccessor>()
                .SetTenant(new TenantContext(tenant.Id, tenant.Slug, tenant.DisplayName, true, tenant.IsDemo));
            if (historyClamped)
                scope.ServiceProvider.GetRequiredService<ICategoryReadContext>().ClampMemberHistory();
            latest = await scope.ServiceProvider
                .GetRequiredService<ICanonicalGlucoseService>()
                .GetLatestAsync(ct);
        }

        await using var db = await _factory.CreateTenantPinnedContextAsync(tenant.Id, ct);

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
