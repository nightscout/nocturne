using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Services.Identity;

[Trait("Category", "Unit")]
public class TenantOverviewServiceTests
{
    private static readonly TenantOverviewThresholds Defaults = new(55, 80, 180, 260);

    /// <summary>Token scopes equivalent to a session/full-access token.</summary>
    private static readonly IReadOnlySet<string> FullTokenScopes =
        new HashSet<string> { Scope.FullAccess };

    // ---- threshold resolution ----

    [Fact]
    public void ResolveThresholds_noRules_returnsDefaults()
    {
        TenantOverviewService.ResolveThresholds(Defaults, []).Should().Be(Defaults);
    }

    [Fact]
    public void ResolveThresholds_overridesEachBucketFromRules()
    {
        var resolved = TenantOverviewService.ResolveThresholds(Defaults,
        [
            ("below", 60, AlertRuleSeverity.Critical),
            ("below", 90, AlertRuleSeverity.Warning),
            ("above", 170, AlertRuleSeverity.Warning),
            ("above", 250, AlertRuleSeverity.Critical),
        ]);

        resolved.Should().Be(new TenantOverviewThresholds(60, 90, 170, 250));
    }

    [Fact]
    public void ResolveThresholds_infoSeverityFillsTheNonUrgentBuckets()
    {
        var resolved = TenantOverviewService.ResolveThresholds(Defaults,
        [
            ("below", 85, AlertRuleSeverity.Info),
            ("above", 190, AlertRuleSeverity.Info),
        ]);

        resolved.Should().Be(new TenantOverviewThresholds(55, 85, 190, 260));
    }

    [Fact]
    public void ResolveThresholds_multipleRulesInABucket_mostConservativeWins()
    {
        var resolved = TenantOverviewService.ResolveThresholds(Defaults,
        [
            // below: highest value is most conservative
            ("below", 70, AlertRuleSeverity.Warning),
            ("below", 85, AlertRuleSeverity.Warning),
            // above: lowest value is most conservative
            ("above", 200, AlertRuleSeverity.Warning),
            ("above", 170, AlertRuleSeverity.Warning),
        ]);

        resolved.Low.Should().Be(85);
        resolved.High.Should().Be(170);
    }

    [Fact]
    public void ResolveThresholds_clampsUrgentBoundsToOrdering()
    {
        var resolved = TenantOverviewService.ResolveThresholds(Defaults,
        [
            ("below", 90, AlertRuleSeverity.Critical),  // urgent-low above default low (80)
            ("above", 150, AlertRuleSeverity.Critical), // urgent-high below default high (180)
        ]);

        resolved.UrgentLow.Should().BeLessThanOrEqualTo(resolved.Low);
        resolved.UrgentHigh.Should().BeGreaterThanOrEqualTo(resolved.High);
        resolved.UrgentLow.Should().Be(80);
        resolved.UrgentHigh.Should().Be(180);
    }

    [Fact]
    public void ResolveThresholds_invertedBand_lowIsClampedToHigh()
    {
        // A Warning "below 200" with default High=180 would put Low above High.
        var resolved = TenantOverviewService.ResolveThresholds(Defaults,
            [("below", 200, AlertRuleSeverity.Warning)]);

        resolved.Low.Should().Be(180);
        resolved.High.Should().Be(180);
        resolved.UrgentLow.Should().BeLessThanOrEqualTo(resolved.Low);

        // A reading of 190 must not classify Low.
        TenantOverviewService.Classify(190, Now.AddMinutes(-1), null, resolved, StaleAfter, Now)
            .Should().Be(GlucoseStatus.High);
    }

    [Fact]
    public void ResolveThresholds_directionCasingIsIgnored()
    {
        var resolved = TenantOverviewService.ResolveThresholds(Defaults,
            [("Below", 70, AlertRuleSeverity.Critical)]);

        resolved.UrgentLow.Should().Be(70);
    }

    // ---- classification ----

    private static readonly DateTime Now = new(2026, 07, 04, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(25);

    private static GlucoseStatus ClassifyValue(double mgdl) =>
        TenantOverviewService.Classify(mgdl, Now.AddMinutes(-5), Now.AddMinutes(-5), Defaults, StaleAfter, Now);

    [Theory]
    [InlineData(54, GlucoseStatus.UrgentLow)]
    [InlineData(55, GlucoseStatus.Low)]        // boundary: not urgent
    [InlineData(79, GlucoseStatus.Low)]
    [InlineData(80, GlucoseStatus.InRange)]    // boundary: in range
    [InlineData(120, GlucoseStatus.InRange)]
    [InlineData(180, GlucoseStatus.InRange)]   // boundary: in range
    [InlineData(181, GlucoseStatus.High)]
    [InlineData(260, GlucoseStatus.High)]      // boundary: not urgent
    [InlineData(261, GlucoseStatus.UrgentHigh)]
    public void Classify_valueAgainstThresholds(double mgdl, GlucoseStatus expected)
    {
        ClassifyValue(mgdl).Should().Be(expected);
    }

    [Fact]
    public void Classify_readingOlderThanStaleWindow_isStale()
    {
        TenantOverviewService.Classify(120, Now.AddMinutes(-26), null, Defaults, StaleAfter, Now)
            .Should().Be(GlucoseStatus.Stale);
    }

    [Fact]
    public void Classify_readingAtExactlyTheStaleWindow_isNotStale()
    {
        TenantOverviewService.Classify(120, Now - StaleAfter, null, Defaults, StaleAfter, Now)
            .Should().Be(GlucoseStatus.InRange);
    }

    [Fact]
    public void Classify_noReadingAndNoLastReadingAt_isUnknown()
    {
        TenantOverviewService.Classify(null, null, null, Defaults, StaleAfter, Now)
            .Should().Be(GlucoseStatus.Unknown);
    }

    [Fact]
    public void Classify_noReadingButStaleLastReadingAt_isStale()
    {
        TenantOverviewService.Classify(null, null, Now.AddHours(-2), Defaults, StaleAfter, Now)
            .Should().Be(GlucoseStatus.Stale);
    }

    [Fact]
    public void Classify_noReadingWithFreshLastReadingAt_isUnknown()
    {
        TenantOverviewService.Classify(null, null, Now.AddMinutes(-1), Defaults, StaleAfter, Now)
            .Should().Be(GlucoseStatus.Unknown);
    }

    // ---- membership filtering ----

    [Fact]
    public async Task GetOverview_filtersMembershipsByPermissionAndTenantState()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();

        SeedMembership(options, subjectId, "included", [Scope.GlucoseRead]);
        SeedMembership(options, subjectId, "superuser", [Scope.FullAccess]);
        SeedMembership(options, subjectId, "readwrite", [Scope.GlucoseReadWrite]);
        SeedMembership(options, subjectId, "direct-only", rolePermissions: null,
            directPermissions: [Scope.GlucoseRead]);
        SeedMembership(options, subjectId, "no-glucose", [Scope.TreatmentsRead]);
        SeedMembership(options, subjectId, "revoked", [Scope.GlucoseRead], revoked: true);
        SeedMembership(options, subjectId, "inactive", [Scope.GlucoseRead], tenantActive: false);
        SeedMembership(options, Guid.NewGuid(), "other-subject", [Scope.GlucoseRead]);

        var service = NewService(options);
        var response = await service.GetOverviewAsync(subjectId, FullTokenScopes, AuthType.OAuthAccessToken);

        response.Tenants.Select(t => t.Slug).Should().BeEquivalentTo(
            ["included", "superuser", "readwrite", "direct-only"]);
        response.Tenants.Should().OnlyContain(t =>
            t.Status == GlucoseStatus.Unknown && t.Latest == null);
    }

    // ---- token scope gating ----

    [Fact]
    public async Task GetOverview_delegatedTokenWithoutGlucoseScope_excludesEveryTenant()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();

        SeedMembership(options, subjectId, "member-tenant", [Scope.GlucoseRead]);
        SeedMembership(options, subjectId, "owner-tenant", [Scope.FullAccess]);

        var service = NewService(options);

        // A delegated token bounds every membership, the owner's included: an app authorized for
        // treatments.read must not be handed glucose through a "*" membership. The superuser
        // membership used to bypass the intersection here, so an owner's narrowly-scoped
        // third-party token still saw every tenant in the picker.
        var narrow = await service.GetOverviewAsync(
            subjectId, new HashSet<string> { Scope.TreatmentsRead }, AuthType.OAuthAccessToken);
        narrow.Tenants.Should().BeEmpty();

        // An empty scope set on a delegated credential grants nothing.
        var empty = await service.GetOverviewAsync(
            subjectId, new HashSet<string>(), AuthType.OAuthAccessToken);
        empty.Tenants.Should().BeEmpty();

        // A full-access token consents to everything, so both memberships resolve in full.
        var full = await service.GetOverviewAsync(subjectId, FullTokenScopes, AuthType.OAuthAccessToken);
        full.Tenants.Select(t => t.Slug).Should().BeEquivalentTo(["member-tenant", "owner-tenant"]);
    }

    [Fact]
    public async Task GetOverview_unscopedSessionCredential_seesTenantsFromTheMembershipAlone()
    {
        // A browser session carries no scope grant: its JWT permission claims come from the
        // subject's GLOBAL roles, which are empty for a member who joined by invite. Intersecting
        // against them emptied the tenant picker for every non-owner membership.
        var subjectId = Guid.NewGuid();
        var options = NewOptions();

        SeedMembership(options, subjectId, "member-tenant", [Scope.GlucoseRead]);
        SeedMembership(options, subjectId, "admin-tenant",
            RoleSeeds.Permissions[RoleSeeds.Admin]);
        SeedMembership(options, subjectId, "owner-tenant", [Scope.FullAccess]);
        SeedMembership(options, subjectId, "no-glucose", [Scope.TreatmentsRead]);
        SeedMembership(options, subjectId, "denied",
            RoleSeeds.Permissions[RoleSeeds.Denied]);

        var service = NewService(options);

        var session = await service.GetOverviewAsync(
            subjectId, new HashSet<string>(), AuthType.SessionCookie);
        session.Tenants.Select(t => t.Slug).Should().BeEquivalentTo(
            ["member-tenant", "admin-tenant", "owner-tenant"]);

        // The same subject over a delegated credential with no scopes still sees nothing.
        var delegated = await service.GetOverviewAsync(
            subjectId, new HashSet<string>(), AuthType.OAuthAccessToken);
        delegated.Tenants.Should().BeEmpty();
    }

    // ---- hub seam ----
    //
    // The overview hub derives its group set from GetGlucoseReadTenantsAsync, so the seam must
    // gate on credential type exactly as the endpoint does or the hub streams a tenant the
    // endpoint refuses.

    [Fact]
    public async Task GetGlucoseReadTenants_delegatedTokenWithoutGlucoseScope_excludesEveryTenant()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();

        SeedMembership(options, subjectId, "member-tenant", [Scope.GlucoseRead]);
        SeedMembership(options, subjectId, "owner-tenant", [Scope.FullAccess]);

        var service = NewService(options);

        var narrow = await service.GetGlucoseReadTenantsAsync(
            subjectId, new HashSet<string> { Scope.TreatmentsRead }, AuthType.OAuthAccessToken);
        narrow.Should().BeEmpty();

        var empty = await service.GetGlucoseReadTenantsAsync(
            subjectId, new HashSet<string>(), AuthType.OAuthAccessToken);
        empty.Should().BeEmpty();

        var full = await service.GetGlucoseReadTenantsAsync(
            subjectId, FullTokenScopes, AuthType.OAuthAccessToken);
        full.Select(t => t.Tenant.Slug).Should().BeEquivalentTo(["member-tenant", "owner-tenant"]);
    }

    [Fact]
    public async Task GetGlucoseReadTenants_unscopedSessionCredential_seesTenantsFromTheMembershipAlone()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();

        SeedMembership(options, subjectId, "member-tenant", [Scope.GlucoseRead]);
        SeedMembership(options, subjectId, "admin-tenant",
            RoleSeeds.Permissions[RoleSeeds.Admin]);
        SeedMembership(options, subjectId, "no-glucose", [Scope.TreatmentsRead]);
        SeedMembership(options, subjectId, "denied",
            RoleSeeds.Permissions[RoleSeeds.Denied]);

        var service = NewService(options);

        var session = await service.GetGlucoseReadTenantsAsync(
            subjectId, new HashSet<string>(), AuthType.SessionCookie);
        session.Select(t => t.Tenant.Slug).Should().BeEquivalentTo(["member-tenant", "admin-tenant"]);

        var delegated = await service.GetGlucoseReadTenantsAsync(
            subjectId, new HashSet<string>(), AuthType.OAuthAccessToken);
        delegated.Should().BeEmpty();
    }

    [Fact]
    public async Task GetGlucoseReadTenants_superuserMembership_widensOnlyForUnscopedCredentials()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        SeedMembership(options, subjectId, "owner-tenant", [Scope.FullAccess]);

        var service = NewService(options);

        // Unscoped credential: membership is the whole authority, so "*" reaches the caller.
        var session = await service.GetGlucoseReadTenantsAsync(
            subjectId, new HashSet<string>(), AuthType.SessionCookie);
        session.Single().AllowedScopes.Should().Contain(Scope.FullAccess);

        // Delegated credential: the consented scope list is a ceiling the "*" membership
        // must not widen past.
        var delegated = await service.GetGlucoseReadTenantsAsync(
            subjectId, new HashSet<string> { Scope.GlucoseRead }, AuthType.OAuthAccessToken);
        delegated.Single().AllowedScopes.Should().NotContain(Scope.FullAccess);
        delegated.Single().AllowedScopes.Should().Contain(Scope.GlucoseRead);
    }

    [Fact]
    public async Task GetOverview_tokenWithGlucoseButNotAlerts_getsGlucoseWithoutAlertFields()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        SeedMembership(options, subjectId, "family",
            [Scope.GlucoseRead, Scope.AlertsRead]);

        var service = NewService(options);
        var response = await service.GetOverviewAsync(
            subjectId, new HashSet<string> { Scope.GlucoseRead }, AuthType.OAuthAccessToken);

        var item = response.Tenants.Should().ContainSingle().Subject;
        item.ActiveAlertCount.Should().BeNull();
        item.HighestActiveSeverity.Should().BeNull();
    }

    // ---- alert permission gating ----

    [Fact]
    public async Task GetOverview_memberWithoutAlertsRead_hasNullAlertFields()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        var tenantId = SeedMembership(options, subjectId, "family", [Scope.GlucoseRead]);
        SeedActiveExcursion(options, tenantId, AlertRuleSeverity.Warning);

        var service = NewService(options);
        var response = await service.GetOverviewAsync(subjectId, FullTokenScopes, AuthType.OAuthAccessToken);

        var item = response.Tenants.Should().ContainSingle().Subject;
        item.ActiveAlertCount.Should().BeNull();
        item.HighestActiveSeverity.Should().BeNull();
    }

    // ---- item building ----

    [Fact]
    public async Task GetOverview_classifiesLatestReadingAndCountsActiveAlerts()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();

        var tenantId = SeedMembership(options, subjectId, "family",
            [Scope.GlucoseRead, Scope.AlertsRead]);

        // One active excursion (warning) + one ended (critical, must not count).
        using (var db = new NocturneDbContext(options) { TenantId = tenantId })
        {
            var warningRule = new AlertRuleEntity
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Severity = AlertRuleSeverity.Warning,
                ConditionType = AlertConditionType.SignalLoss,
            };
            var criticalRule = new AlertRuleEntity
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Severity = AlertRuleSeverity.Critical,
                ConditionType = AlertConditionType.Threshold,
                ConditionParams = """{"direction":"below","value":65}""",
            };
            db.AlertRules.AddRange(warningRule, criticalRule);
            db.AlertExcursions.AddRange(
                new AlertExcursionEntity
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    AlertRuleId = warningRule.Id,
                    StartedAt = DateTime.UtcNow.AddMinutes(-10),
                },
                new AlertExcursionEntity
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    AlertRuleId = criticalRule.Id,
                    StartedAt = DateTime.UtcNow.AddHours(-3),
                    EndedAt = DateTime.UtcNow.AddHours(-2),
                });
            db.SaveChanges();
        }

        var latest = new SensorGlucose
        {
            Mgdl = 62,
            Timestamp = DateTime.UtcNow.AddMinutes(-3),
            Direction = GlucoseDirection.SingleDown,
            Delta = -8,
            TrendRate = -1.6,
        };
        var service = NewService(options, latest);

        var response = await service.GetOverviewAsync(subjectId, FullTokenScopes, AuthType.OAuthAccessToken);

        var item = response.Tenants.Should().ContainSingle().Subject;
        item.Latest.Should().NotBeNull();
        item.Latest!.Mgdl.Should().Be(62);
        item.Latest.Direction.Should().Be(GlucoseDirection.SingleDown);
        // The enabled critical below-65 rule overrides the urgent-low default (55).
        item.Thresholds.UrgentLow.Should().Be(65);
        item.Status.Should().Be(GlucoseStatus.UrgentLow);
        item.ActiveAlertCount.Should().Be(1);
        item.HighestActiveSeverity.Should().Be(AlertRuleSeverity.Warning);
        item.LastReadingAt.Should().Be(latest.Timestamp);
    }

    [Fact]
    public async Task GetOverview_otherTenantsRulesAndExcursions_doNotLeakAcrossTenants()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();

        var tenantA = SeedMembership(options, subjectId, "tenant-a",
            [Scope.GlucoseRead, Scope.AlertsRead]);
        var tenantB = SeedMembership(options, Guid.NewGuid(), "tenant-b", [Scope.GlucoseRead]);

        // Tenant B: an active critical excursion and a threshold rule that would move UrgentLow.
        SeedActiveExcursion(options, tenantB, AlertRuleSeverity.Critical,
            conditionParams: """{"direction":"below","value":100}""");

        var latest = new SensorGlucose { Mgdl = 120, Timestamp = DateTime.UtcNow.AddMinutes(-2) };
        var service = NewService(options, latest);
        var response = await service.GetOverviewAsync(subjectId, FullTokenScopes, AuthType.OAuthAccessToken);

        var item = response.Tenants.Should().ContainSingle().Subject;
        item.TenantId.Should().Be(tenantA);
        item.ActiveAlertCount.Should().Be(0);
        item.HighestActiveSeverity.Should().BeNull();
        item.Thresholds.Should().Be(Defaults);
    }

    [Fact]
    public async Task GetOverview_oneTenantFailing_returnsFallbackItemAndOthersIntact()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();

        var okTenant = SeedMembership(options, subjectId, "ok-tenant", [Scope.GlucoseRead]);
        var badTenant = SeedMembership(options, subjectId, "bad-tenant", [Scope.GlucoseRead]);

        var latest = new SensorGlucose { Mgdl = 120, Timestamp = DateTime.UtcNow.AddMinutes(-2) };
        var service = NewService(options, latest, failingTenantId: badTenant);

        var response = await service.GetOverviewAsync(subjectId, FullTokenScopes, AuthType.OAuthAccessToken);

        response.Tenants.Should().HaveCount(2);

        var ok = response.Tenants.Single(t => t.TenantId == okTenant);
        ok.Status.Should().Be(GlucoseStatus.InRange);
        ok.Latest.Should().NotBeNull();

        var bad = response.Tenants.Single(t => t.TenantId == badTenant);
        bad.Status.Should().Be(GlucoseStatus.Unknown);
        bad.Latest.Should().BeNull();
        bad.ActiveAlertCount.Should().BeNull();
        bad.HighestActiveSeverity.Should().BeNull();
        bad.Thresholds.Should().Be(Defaults);
    }

    [Fact]
    public async Task GetOverview_malformedThresholdRule_isSkippedAndReadingPreserved()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        var tenantId = SeedMembership(options, subjectId, "family", [Scope.GlucoseRead]);

        using (var db = new NocturneDbContext(options) { TenantId = tenantId })
        {
            db.AlertRules.AddRange(
                new AlertRuleEntity
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    Severity = AlertRuleSeverity.Warning,
                    ConditionType = AlertConditionType.Threshold,
                    ConditionParams = """{"value":90}""", // no direction
                },
                new AlertRuleEntity
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    Severity = AlertRuleSeverity.Critical,
                    ConditionType = AlertConditionType.Threshold,
                    ConditionParams = """{"direction":"below","value":60}""",
                });
            db.SaveChanges();
        }

        var latest = new SensorGlucose { Mgdl = 120, Timestamp = DateTime.UtcNow.AddMinutes(-2) };
        var service = NewService(options, latest);
        var response = await service.GetOverviewAsync(subjectId, FullTokenScopes, AuthType.OAuthAccessToken);

        var item = response.Tenants.Should().ContainSingle().Subject;
        // The bad rule is skipped, the good rule still applies, and the reading survives.
        item.Latest.Should().NotBeNull();
        item.Status.Should().Be(GlucoseStatus.InRange);
        item.Thresholds.UrgentLow.Should().Be(60);
        item.Thresholds.Low.Should().Be(Defaults.Low);
    }

    // ---- helpers ----

    private static DbContextOptions<NocturneDbContext> NewOptions() =>
        new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    private static Guid SeedMembership(
        DbContextOptions<NocturneDbContext> options,
        Guid subjectId,
        string slug,
        List<string>? rolePermissions,
        List<string>? directPermissions = null,
        bool revoked = false,
        bool tenantActive = true)
    {
        using var db = new NocturneDbContext(options);
        var tenant = new TenantEntity
        {
            Id = Guid.NewGuid(),
            Slug = slug,
            DisplayName = slug,
            IsActive = tenantActive,
        };
        db.Tenants.Add(tenant);

        var member = new TenantMemberEntity
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            SubjectId = subjectId,
            DirectPermissions = directPermissions,
            RevokedAt = revoked ? DateTime.UtcNow.AddDays(-1) : null,
        };
        db.TenantMembers.Add(member);

        if (rolePermissions is not null)
        {
            var role = new TenantRoleEntity
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.Id,
                Name = "role",
                Slug = $"role-{slug}",
                Permissions = rolePermissions,
            };
            db.TenantRoles.Add(role);
            db.TenantMemberRoles.Add(new TenantMemberRoleEntity
            {
                Id = Guid.NewGuid(),
                TenantMemberId = member.Id,
                TenantRoleId = role.Id,
            });
        }

        db.SaveChanges();
        return tenant.Id;
    }

    private static void SeedActiveExcursion(
        DbContextOptions<NocturneDbContext> options,
        Guid tenantId,
        AlertRuleSeverity severity,
        string? conditionParams = null)
    {
        using var db = new NocturneDbContext(options) { TenantId = tenantId };
        var rule = new AlertRuleEntity
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Severity = severity,
            ConditionType = AlertConditionType.Threshold,
            ConditionParams = conditionParams ?? """{"direction":"below","value":70}""",
        };
        db.AlertRules.Add(rule);
        db.AlertExcursions.Add(new AlertExcursionEntity
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            AlertRuleId = rule.Id,
            StartedAt = DateTime.UtcNow.AddMinutes(-10),
        });
        db.SaveChanges();
    }

    private static TenantOverviewService NewService(
        DbContextOptions<NocturneDbContext> options,
        SensorGlucose? latest = null,
        Guid? failingTenantId = null)
    {
        TenantContext? current = null;

        var accessor = new Mock<ITenantAccessor>();
        accessor.Setup(a => a.SetTenant(It.IsAny<TenantContext>()))
            .Callback<TenantContext>(tc => current = tc);

        var canonical = new Mock<ICanonicalGlucoseService>();
        canonical.Setup(s => s.GetLatestAsync(It.IsAny<CancellationToken>()))
            .Returns(() => current?.TenantId == failingTenantId
                ? Task.FromException<SensorGlucose?>(new InvalidOperationException("boom"))
                : Task.FromResult(latest));

        var services = new ServiceCollection();
        services.AddScoped(_ => canonical.Object);
        services.AddScoped(_ => accessor.Object);
        var provider = services.BuildServiceProvider();

        return new TenantOverviewService(
            new InMemoryContextFactory(options),
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().Build(),
            NullLogger<TenantOverviewService>.Instance);
    }

    private sealed class InMemoryContextFactory(DbContextOptions<NocturneDbContext> options)
        : IDbContextFactory<NocturneDbContext>
    {
        public NocturneDbContext CreateDbContext() => new(options);
    }
}
