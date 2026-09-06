using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Constants;
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
    private readonly IConfiguration _configuration;
    private readonly ILogger<TenantOverviewService> _logger;

    public TenantOverviewService(
        IDbContextFactory<NocturneDbContext> factory,
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<TenantOverviewService> logger)
    {
        _factory = factory;
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<TenantOverviewResponse> GetOverviewAsync(
        Guid subjectId, IReadOnlySet<string> tokenScopes, AuthType authType,
        CancellationToken ct = default)
    {
        var glucoseReadTenants = await GetGlucoseReadTenantsAsync(subjectId, tokenScopes, authType, ct);

        var defaults = new TenantOverviewThresholds(
            UrgentLow: _configuration.GetValue("Thresholds:BgLow", ApplicationConstants.Web.Thresholds.BgLow),
            Low: _configuration.GetValue("Thresholds:BgTargetBottom", ApplicationConstants.Web.Thresholds.BgTargetBottom),
            High: _configuration.GetValue("Thresholds:BgTargetTop", ApplicationConstants.Web.Thresholds.BgTargetTop),
            UrgentHigh: _configuration.GetValue("Thresholds:BgHigh", ApplicationConstants.Web.Thresholds.BgHigh));
        var staleAfter = TimeSpan.FromMinutes(_configuration.GetValue("Overview:StaleAfterMinutes", 25));

        var items = new List<TenantOverviewItem>();
        foreach (var (tenant, allowed) in glucoseReadTenants)
        {
            var includeAlerts = Scope.Satisfies(allowed, Scope.AlertsRead);

            try
            {
                items.Add(await BuildItemAsync(tenant, defaults, staleAfter, includeAlerts, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to build overview for tenant {TenantId}", tenant.Id);
                items.Add(new TenantOverviewItem(
                    tenant.Id, tenant.Slug, tenant.DisplayName,
                    tenant.LastReadingAt, Latest: null, GlucoseStatus.Unknown,
                    defaults, ActiveAlertCount: null, HighestActiveSeverity: null));
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

            result.Add(new GlucoseReadTenant(tenant, allowed));
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
        TenantMemberEntity membership, IReadOnlySet<string> tokenScopes, AuthType authType)
    {
        var effective = membership.MemberRoles
            .SelectMany(mr => mr.TenantRole.Permissions)
            .Union(membership.DirectPermissions ?? [])
            .ToHashSet();

        return MemberScopeResolver.Resolve(effective, authType, tokenScopes);
    }

    private async Task<TenantOverviewItem> BuildItemAsync(
        TenantEntity tenant,
        TenantOverviewThresholds defaults,
        TimeSpan staleAfter,
        bool includeAlerts,
        CancellationToken ct)
    {
        // Fresh scope per tenant: CanonicalGlucoseService caches per scope.
        SensorGlucose? latest;
        using (var scope = _scopeFactory.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<ITenantAccessor>()
                .SetTenant(new TenantContext(tenant.Id, tenant.Slug, tenant.DisplayName, true, tenant.IsDemo));
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

        var thresholdRules = await db.AlertRules.AsNoTracking()
            .Where(r => r.IsEnabled && r.ConditionType == AlertConditionType.Threshold)
            .ToListAsync(ct);

        var thresholds = ResolveThresholds(defaults, ParseThresholdRules(thresholdRules));

        var reading = latest is null
            ? null
            : new TenantOverviewReading(latest.Mgdl, latest.Delta, latest.Direction, latest.TrendRate, latest.Timestamp);

        var status = Classify(
            latest?.Mgdl, latest?.Timestamp, tenant.LastReadingAt, thresholds, staleAfter, DateTime.UtcNow);

        return new TenantOverviewItem(
            tenant.Id, tenant.Slug, tenant.DisplayName,
            latest?.Timestamp ?? tenant.LastReadingAt,
            reading, status, thresholds,
            activeAlertCount, highestSeverity);
    }

    private IEnumerable<(string Direction, double Value, AlertRuleSeverity Severity)> ParseThresholdRules(
        IEnumerable<AlertRuleEntity> rules)
    {
        foreach (var rule in rules)
        {
            ThresholdCondition? condition;
            try
            {
                condition = JsonSerializer.Deserialize<ThresholdCondition>(rule.ConditionParams, EvaluatorJson.Options);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Unparseable threshold condition params for rule {RuleId}", rule.Id);
                continue;
            }

            // STJ materializes the record with a null Direction when the property is
            // absent from the JSON; such a rule cannot be bucketed.
            if (condition is null || string.IsNullOrEmpty(condition.Direction))
            {
                if (condition is not null)
                    _logger.LogWarning("Threshold rule {RuleId} has no direction; skipped", rule.Id);
                continue;
            }

            yield return (condition.Direction, (double)condition.Value, rule.Severity);
        }
    }

    /// <summary>
    /// Overrides configuration defaults from the tenant's enabled threshold rules.
    /// Multiple rules in the same bucket resolve to the most conservative value
    /// (below: highest, above: lowest); urgent bounds are then clamped so
    /// UrgentLow &lt;= Low and High &lt;= UrgentHigh, and finally Low is clamped
    /// to High so the in-range band cannot invert.
    /// </summary>
    internal static TenantOverviewThresholds ResolveThresholds(
        TenantOverviewThresholds defaults,
        IEnumerable<(string Direction, double Value, AlertRuleSeverity Severity)> rules)
    {
        double? urgentLow = null, low = null, high = null, urgentHigh = null;

        foreach (var (direction, value, severity) in rules)
        {
            if (string.IsNullOrEmpty(direction)) continue;

            switch (direction.ToLowerInvariant(), severity)
            {
                case ("below", AlertRuleSeverity.Critical):
                    urgentLow = Math.Max(urgentLow ?? double.MinValue, value);
                    break;
                case ("below", _):
                    low = Math.Max(low ?? double.MinValue, value);
                    break;
                case ("above", AlertRuleSeverity.Critical):
                    urgentHigh = Math.Min(urgentHigh ?? double.MaxValue, value);
                    break;
                case ("above", _):
                    high = Math.Min(high ?? double.MaxValue, value);
                    break;
            }
        }

        var resolved = new TenantOverviewThresholds(
            urgentLow ?? defaults.UrgentLow,
            low ?? defaults.Low,
            high ?? defaults.High,
            urgentHigh ?? defaults.UrgentHigh);

        resolved = resolved with
        {
            UrgentLow = Math.Min(resolved.UrgentLow, resolved.Low),
            UrgentHigh = Math.Max(resolved.UrgentHigh, resolved.High),
        };

        // A "below" rule above the high bound (e.g. below-200 with High=180) would invert
        // the in-range band; keep Low <= High (and UrgentLow <= the clamped Low) so
        // classification stays ordered.
        var clampedLow = Math.Min(resolved.Low, resolved.High);
        return resolved with
        {
            Low = clampedLow,
            UrgentLow = Math.Min(resolved.UrgentLow, clampedLow),
        };
    }

    /// <summary>
    /// Classifies the latest reading. Boundary values are in range / non-urgent
    /// (strict comparisons throughout).
    /// </summary>
    internal static GlucoseStatus Classify(
        double? mgdl,
        DateTime? readingTimestamp,
        DateTime? lastReadingAt,
        TenantOverviewThresholds thresholds,
        TimeSpan staleAfter,
        DateTime nowUtc)
    {
        var freshness = readingTimestamp ?? lastReadingAt;
        if (freshness is null) return GlucoseStatus.Unknown;
        if (nowUtc - freshness.Value > staleAfter) return GlucoseStatus.Stale;
        if (mgdl is null) return GlucoseStatus.Unknown;

        return mgdl.Value switch
        {
            var v when v < thresholds.UrgentLow => GlucoseStatus.UrgentLow,
            var v when v < thresholds.Low => GlucoseStatus.Low,
            var v when v > thresholds.UrgentHigh => GlucoseStatus.UrgentHigh,
            var v when v > thresholds.High => GlucoseStatus.High,
            _ => GlucoseStatus.InRange,
        };
    }
}
