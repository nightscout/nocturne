using Nocturne.Tests.Shared.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Glucose;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.API.Services.Analytics;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Models.Widget;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;
using Xunit;

namespace Nocturne.API.Tests.Services.Identity;

[Trait("Category", "Unit")]
public class TenantOverviewServiceTests
{
    private static readonly TenantOverviewThresholds Defaults = new(55, 80, 180, 260);

    /// <summary>Token scopes equivalent to a session/full-access token.</summary>
    private static readonly IReadOnlySet<string> FullTokenScopes =
        new HashSet<string> { Scope.FullAccess };

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
        var fakes = new Fakes();
        fakes.Data[tenantB] = new TenantData
        {
            Recent = [new SensorGlucose { Mgdl = 40, Timestamp = DateTime.UtcNow.AddMinutes(-1) }],
            OnBoard = OnBoard(iob: 9, cob: 90),
            Pump = new PumpSnapshot { Reservoir = 3, BatteryPercent = 4, Timestamp = DateTime.UtcNow },
            Trackers = [EventTracker("b-tracker", hoursUntil: 1, NotificationUrgency.Urgent)],
        };
        var service = NewService(options, latest, fakes: fakes);
        var response = await service.GetOverviewAsync(subjectId, FullTokenScopes, AuthType.OAuthAccessToken);

        var item = response.Tenants.Should().ContainSingle().Subject;
        item.TenantId.Should().Be(tenantA);
        item.ActiveAlertCount.Should().Be(0);
        item.HighestActiveSeverity.Should().BeNull();
        item.Thresholds.Should().Be(Defaults);
        item.RecentReadings.Should().BeEmpty();
        item.Therapy.Should().BeNull();
        item.Devices.Should().BeNull();
        item.Trackers.Should().BeEmpty();
        item.Attention.Should().BeEmpty();
    }

    [Fact]
    public async Task GetOverview_twoTenantsInOneCall_eachItemCarriesOnlyItsOwnData()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        var all = new List<string> { Scope.GlucoseRead, Scope.TreatmentsRead, Scope.DevicesRead, Scope.AlertsRead };
        var tenantA = SeedMembership(options, subjectId, "tenant-a", all);
        var tenantB = SeedMembership(options, subjectId, "tenant-b", all);

        var now = DateTime.UtcNow;
        var fakes = new Fakes();
        fakes.Data[tenantA] = new TenantData
        {
            Latest = new SensorGlucose { Mgdl = 100, Timestamp = now.AddMinutes(-1) },
            Recent = [new SensorGlucose { Mgdl = 100, Timestamp = now.AddMinutes(-1) }],
            OnBoard = OnBoard(iob: 1.5, cob: 10),
            Pump = new PumpSnapshot { Reservoir = 120, BatteryPercent = 80, Timestamp = now.AddMinutes(-2) },
            Trackers = [EventTracker("a-site", hoursUntil: 1, NotificationUrgency.Warn)],
        };
        fakes.Data[tenantB] = new TenantData
        {
            Latest = new SensorGlucose { Mgdl = 50, Timestamp = now.AddMinutes(-1) },
            Recent = [new SensorGlucose { Mgdl = 50, Timestamp = now.AddMinutes(-1) }],
            OnBoard = OnBoard(iob: 4, cob: 40),
            Pump = new PumpSnapshot { Reservoir = 10, BatteryPercent = 5, Timestamp = now.AddMinutes(-3) },
            Trackers = [EventTracker("b-sensor", hoursUntil: 1, NotificationUrgency.Hazard)],
        };

        var response = await NewService(options, fakes: fakes)
            .GetOverviewAsync(subjectId, FullTokenScopes, AuthType.OAuthAccessToken);

        var a = response.Tenants.Single(t => t.TenantId == tenantA);
        a.RecentReadings.Select(r => r.Mgdl).Should().Equal(100);
        a.Therapy.Should().Be(new TenantOverviewTherapy(1.5, true, 10, true));
        a.Devices!.ReservoirUnits.Should().Be(120);
        a.Trackers!.Select(t => t.Name).Should().Equal("a-site");
        a.Attention.Select(r => r.TrackerName).Should().NotContain("b-sensor");

        var b = response.Tenants.Single(t => t.TenantId == tenantB);
        b.RecentReadings.Select(r => r.Mgdl).Should().Equal(50);
        b.Therapy.Should().Be(new TenantOverviewTherapy(4, true, 40, true));
        b.Devices!.ReservoirUnits.Should().Be(10);
        b.Trackers!.Select(t => t.Name).Should().Equal("b-sensor");
        b.Attention.Select(r => r.TrackerName).Should().NotContain("a-site");
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
        bad.RecentReadings.Should().BeEmpty();
        bad.Therapy.Should().BeNull();
        bad.Devices.Should().BeNull();
        bad.Trackers.Should().BeNull();
        bad.Attention.Should().ContainSingle().Which.Should().Be(
            new TenantOverviewAttention(TenantOverviewAttentionKind.NoRecentReading, NotificationUrgency.Warn));
    }

    [Fact]
    public async Task GetOverview_failingTenantWithAFreshLastReading_carriesNoReason()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        var badTenant = SeedMembership(
            options, subjectId, "bad-tenant", [Scope.GlucoseRead],
            lastReadingAt: DateTime.UtcNow.AddMinutes(-2));

        var service = NewService(options, latest: null, failingTenantId: badTenant);

        var response = await service.GetOverviewAsync(subjectId, FullTokenScopes, AuthType.OAuthAccessToken);

        var bad = response.Tenants.Single();
        bad.Status.Should().Be(GlucoseStatus.Unknown);
        bad.Attention.Should().BeEmpty("a reading two minutes old is not a missing reading");
    }

    [Fact]
    public async Task GetOverview_failingTenantWithAnOldLastReading_isStaleWithItsAge()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        var badTenant = SeedMembership(
            options, subjectId, "bad-tenant", [Scope.GlucoseRead],
            lastReadingAt: DateTime.UtcNow.AddHours(-3));

        var service = NewService(options, latest: null, failingTenantId: badTenant);

        var response = await service.GetOverviewAsync(subjectId, FullTokenScopes, AuthType.OAuthAccessToken);

        var bad = response.Tenants.Single();
        bad.Status.Should().Be(GlucoseStatus.Stale);
        var reason = bad.Attention.Should().ContainSingle().Subject;
        reason.Kind.Should().Be(TenantOverviewAttentionKind.NoRecentReading);
        reason.MinutesSinceReading.Should().BeInRange(179, 181);
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

    // ---- history clamp ----

    [Fact]
    public async Task GetOverview_readsEachTenantUnderTheCallersClampThere()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        var oldReading = DateTime.UtcNow.AddDays(-3);
        var clampedTenant = SeedMembership(options, subjectId, "clamped", [Scope.GlucoseRead],
            limitTo24Hours: true, lastReadingAt: oldReading);
        var fullTenant = SeedMembership(options, subjectId, "full", [Scope.GlucoseRead],
            lastReadingAt: oldReading);
        var exemptTenant = SeedMembership(options, subjectId, "exempt", [Scope.TenantSettings, Scope.GlucoseRead],
            limitTo24Hours: true, lastReadingAt: oldReading);
        var ownerTenant = SeedMembership(options, subjectId, "owner", rolePermissions: null,
            directPermissions: [Scope.FullAccess], limitTo24Hours: true, lastReadingAt: oldReading);

        var (service, clampSeen, contextClampSeen) = NewClampObservingService(options);
        var response = await service.GetOverviewAsync(subjectId, FullTokenScopes, AuthType.SessionCookie);

        contextClampSeen.Should().Equal(clampSeen, "the alert and threshold reads run under the same clamp as the glucose read");

        clampSeen[clampedTenant].Should().BeTrue("the glucose read must run under the membership's clamp");
        clampSeen[fullTenant].Should().BeFalse();
        clampSeen[exemptTenant].Should().BeFalse("a tenant.settings holder is never clamped");
        clampSeen[ownerTenant].Should().BeFalse("an owner is never clamped by the membership flag");
        response.Tenants.Single(t => t.Slug == "owner").LastReadingAt.Should().Be(oldReading);
        response.Tenants.Single(t => t.Slug == "clamped").LastReadingAt.Should().BeNull(
            "the denormalised last-reading time would reveal a reading older than 24 hours");
        response.Tenants.Single(t => t.Slug == "full").LastReadingAt.Should().Be(oldReading);
    }

    [Fact]
    public async Task GetOverview_aClampedCredential_isClampedOnEveryTenant()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        var tenantId = SeedMembership(options, subjectId, "full", [Scope.GlucoseRead]);
        var ownerTenant = SeedMembership(options, subjectId, "owner", [Scope.FullAccess]);

        var (service, clampSeen, _) = NewClampObservingService(options);
        await service.GetOverviewAsync(
            subjectId, FullTokenScopes, AuthType.OAuthAccessToken, credentialLimitTo24Hours: true);

        clampSeen[tenantId].Should().BeTrue();
        clampSeen[ownerTenant].Should().BeTrue("the exemption covers the membership flag, not a limit on the token");
    }

    /// <summary>
    /// A service whose per-tenant scopes carry a real <see cref="ICategoryReadContext"/>, recording
    /// whether it was history-clamped when that tenant's latest glucose was read.
    /// </summary>
    private static (TenantOverviewService Service, Dictionary<Guid, bool> ClampSeen, Dictionary<Guid, bool> ContextClampSeen)
        NewClampObservingService(DbContextOptions<NocturneDbContext> options)
    {
        var clampSeen = new Dictionary<Guid, bool>();
        var contextClampSeen = new Dictionary<Guid, bool>();
        var services = new ServiceCollection();
        services.AddScoped<INocturneDbContextFactory>(sp => new ScopeContextFactory(
            options,
            () => sp.GetRequiredService<ITenantAccessor>().TenantId,
            sp.GetRequiredService<ICategoryReadContext>(),
            contextClampSeen));
        services.AddScoped<ICategoryReadContext>(_ => new Nocturne.Infrastructure.Data.Services.CategoryReadContext());
        services.AddScoped<ITenantAccessor>(_ => new RecordingTenantAccessor());
        services.AddScoped<IAlertRepository>(sp => new AlertRepository(sp.GetRequiredService<INocturneDbContextFactory>()));
        services.AddScoped(sp =>
        {
            var category = sp.GetRequiredService<ICategoryReadContext>();
            var accessor = sp.GetRequiredService<ITenantAccessor>();
            var canonical = new Mock<ICanonicalGlucoseService>();
            canonical.Setup(s => s.GetLatestAsync(It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    clampSeen[accessor.TenantId] = category.IsHistoryClamped;
                    return Task.FromResult<SensorGlucose?>(null);
                });
            canonical.Setup(s => s.GetRecentAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<SensorGlucose>());
            return canonical.Object;
        });
        var iobCob = new Mock<IIobCobReader>();
        iobCob.Setup(r => r.ReadTotalAsync(It.IsAny<long>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OnBoard(0, 0, insulin: false, carbs: false));
        services.AddScoped(_ => iobCob.Object);
        var pumps = new Mock<IPumpSnapshotRepository>();
        pumps.Setup(r => r.GetLatestAsync(It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PumpSnapshot?)null);
        services.AddScoped(_ => pumps.Object);
        var trackers = new Mock<ITrackerRepository>();
        trackers.Setup(r => r.GetActiveInstancesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<TrackerInstanceEntity>());
        services.AddScoped(_ => trackers.Object);
        var provider = services.BuildServiceProvider();

        var service = new TenantOverviewService(
            new TenantMembershipRepository((new InMemoryContextFactory(options)).AsNocturneFactory()),
            provider.CreateScope().ServiceProvider,
            new GlucoseStatusClassifier(
                new ConfigurationBuilder().Build(), NullLogger<GlucoseStatusClassifier>.Instance),
            TimeProvider.System,
            NullLogger<TenantOverviewService>.Instance);
        return (service, clampSeen, contextClampSeen);
    }

    private sealed class RecordingTenantAccessor : ITenantAccessor
    {
        public Guid TenantId => Context?.TenantId ?? Guid.Empty;
        public bool IsResolved => Context is not null;
        public TenantContext? Context { get; private set; }
        public void SetTenant(TenantContext context) => Context = context;
    }

    // ---- recent readings ----

    [Fact]
    public async Task GetOverview_recentReadings_areOldestFirstAndOnePerFiveMinuteBucket()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        SeedMembership(options, subjectId, "family", [Scope.GlucoseRead]);

        var bucketStart = new DateTime(
            (DateTime.UtcNow.AddHours(-1).Ticks / TimeSpan.FromMinutes(5).Ticks) * TimeSpan.FromMinutes(5).Ticks,
            DateTimeKind.Utc);
        var fakes = new Fakes();
        fakes.Default = new TenantData
        {
            Recent =
            [
                new SensorGlucose { Mgdl = 130, Timestamp = bucketStart.AddMinutes(10).AddSeconds(30) },
                new SensorGlucose { Mgdl = 120, Timestamp = bucketStart.AddMinutes(5).AddSeconds(10) },
                new SensorGlucose { Mgdl = 111, Timestamp = bucketStart.AddMinutes(4).AddSeconds(50) },
                new SensorGlucose { Mgdl = 110, Timestamp = bucketStart.AddMinutes(2) },
                new SensorGlucose { Mgdl = 100, Timestamp = bucketStart.AddSeconds(5) },
            ],
        };

        var item = (await NewService(options, fakes: fakes)
            .GetOverviewAsync(subjectId, FullTokenScopes, AuthType.OAuthAccessToken)).Tenants.Single();

        item.RecentReadings.Should().Equal(
            new TenantOverviewPoint(bucketStart.AddMinutes(4).AddSeconds(50), 111),
            new TenantOverviewPoint(bucketStart.AddMinutes(5).AddSeconds(10), 120),
            new TenantOverviewPoint(bucketStart.AddMinutes(10).AddSeconds(30), 130));
    }

    [Fact]
    public async Task GetOverview_recentReadings_askForTheLastThreeHoursAndAreEmptyWithNone()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        SeedMembership(options, subjectId, "family", [Scope.GlucoseRead]);
        var fakes = new Fakes();

        var before = DateTime.UtcNow;
        var item = (await NewService(options, fakes: fakes)
            .GetOverviewAsync(subjectId, FullTokenScopes, AuthType.OAuthAccessToken)).Tenants.Single();
        var after = DateTime.UtcNow;

        item.RecentReadings.Should().BeEmpty();
        fakes.Canonical.Verify(s => s.GetRecentAsync(
            It.Is<DateTime>(since => since >= before.AddHours(-3) && since <= after.AddHours(-3)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetOverview_aClampedMember_stillGetsTheThreeHourTrace()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        SeedMembership(options, subjectId, "clamped", [Scope.GlucoseRead], limitTo24Hours: true);
        var fakes = new Fakes();
        fakes.Default = new TenantData
        {
            Recent = [new SensorGlucose { Mgdl = 110, Timestamp = DateTime.UtcNow.AddMinutes(-20) }],
        };

        var item = (await NewService(options, fakes: fakes)
            .GetOverviewAsync(subjectId, FullTokenScopes, AuthType.OAuthAccessToken)).Tenants.Single();

        item.RecentReadings.Should().ContainSingle().Which.Mgdl.Should().Be(110);
    }

    // ---- therapy, devices and trackers: gated by their own scope ----

    [Fact]
    public async Task GetOverview_therapy_isNullWithoutTreatmentsReadAndNeverFetched()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        SeedMembership(options, subjectId, "without", [Scope.GlucoseRead, Scope.DevicesRead, Scope.AlertsRead]);
        var fakes = new Fakes();
        fakes.Default = new TenantData { OnBoard = OnBoard(iob: 2.348, cob: 12.4) };

        var item = (await NewService(options, fakes: fakes)
            .GetOverviewAsync(subjectId, FullTokenScopes, AuthType.OAuthAccessToken)).Tenants.Single();

        item.Therapy.Should().BeNull();
        fakes.IobCob.Verify(
            r => r.ReadTotalAsync(It.IsAny<long>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetOverview_therapy_isPresentWithTreatmentsReadAndKnownOnlyWhenSomethingStandsBehindIt()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        SeedMembership(options, subjectId, "with", [Scope.GlucoseRead, Scope.TreatmentsRead]);
        var fakes = new Fakes();
        fakes.Default = new TenantData { OnBoard = OnBoard(iob: 2.348, cob: 12.4) };

        var service = NewService(options, fakes: fakes);
        var item = (await service.GetOverviewAsync(subjectId, FullTokenScopes, AuthType.OAuthAccessToken))
            .Tenants.Single();
        item.Therapy.Should().Be(new TenantOverviewTherapy(2.35, true, 12, true));

        fakes.Default = new TenantData { OnBoard = OnBoard(iob: 0, cob: 0, insulin: false, carbs: false) };
        item = (await service.GetOverviewAsync(subjectId, FullTokenScopes, AuthType.OAuthAccessToken))
            .Tenants.Single();
        item.Therapy.Should().Be(new TenantOverviewTherapy(0, false, 0, false));
    }

    [Fact]
    public async Task GetOverview_devices_areNullWithoutDevicesReadAndNeverFetched()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        SeedMembership(options, subjectId, "without", [Scope.GlucoseRead, Scope.TreatmentsRead, Scope.AlertsRead]);
        var fakes = new Fakes();
        fakes.Default = new TenantData { Pump = new PumpSnapshot { Reservoir = 50, Timestamp = DateTime.UtcNow } };

        var item = (await NewService(options, fakes: fakes)
            .GetOverviewAsync(subjectId, FullTokenScopes, AuthType.OAuthAccessToken)).Tenants.Single();

        item.Devices.Should().BeNull();
        fakes.Pumps.Verify(r => r.GetLatestAsync(It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetOverview_devices_arePresentWithDevicesReadAndEmptyWithoutASnapshot()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        SeedMembership(options, subjectId, "with", [Scope.GlucoseRead, Scope.DevicesRead]);
        var snapshotAt = DateTime.UtcNow.AddMinutes(-4);
        var fakes = new Fakes();
        fakes.Default = new TenantData
        {
            Pump = new PumpSnapshot { Reservoir = 87.5, BatteryPercent = 60, Timestamp = snapshotAt },
        };

        var service = NewService(options, fakes: fakes);
        var item = (await service.GetOverviewAsync(subjectId, FullTokenScopes, AuthType.OAuthAccessToken))
            .Tenants.Single();
        item.Devices.Should().Be(new TenantOverviewDevices(87.5, 60, snapshotAt));

        fakes.Default = new TenantData();
        item = (await service.GetOverviewAsync(subjectId, FullTokenScopes, AuthType.OAuthAccessToken))
            .Tenants.Single();
        item.Devices.Should().Be(new TenantOverviewDevices(null, null, null));
    }

    [Fact]
    public async Task GetOverview_trackers_areNullWithoutAlertsReadAndNeverFetched()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        SeedMembership(options, subjectId, "without", [Scope.GlucoseRead, Scope.TreatmentsRead, Scope.DevicesRead]);
        var fakes = new Fakes();
        fakes.Default = new TenantData { Trackers = [EventTracker("site", hoursUntil: 1, NotificationUrgency.Urgent)] };

        var item = (await NewService(options, fakes: fakes)
            .GetOverviewAsync(subjectId, FullTokenScopes, AuthType.OAuthAccessToken)).Tenants.Single();

        item.Trackers.Should().BeNull();
        item.Attention.Should().NotContain(r => r.Kind == TenantOverviewAttentionKind.TrackerDue);
        fakes.Trackers.Verify(r => r.GetActiveInstancesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetOverview_trackers_arePresentWithAlertsRead()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        SeedMembership(options, subjectId, "with", [Scope.GlucoseRead, Scope.AlertsRead]);
        var fakes = new Fakes();
        fakes.Default = new TenantData { Trackers = [EventTracker("site", hoursUntil: 1, NotificationUrgency.Warn)] };

        var item = (await NewService(options, fakes: fakes)
            .GetOverviewAsync(subjectId, FullTokenScopes, AuthType.OAuthAccessToken)).Tenants.Single();

        item.Trackers.Should().ContainSingle().Which.Name.Should().Be("site");
    }

    // ---- attention ----

    [Theory]
    [InlineData(50, NotificationUrgency.Urgent)]
    [InlineData(70, NotificationUrgency.Hazard)]
    [InlineData(270, NotificationUrgency.Hazard)]
    public async Task GetOverview_aReadingOutOfRange_buildsGlucoseOutOfRangeAtItsLevel(
        double mgdl, NotificationUrgency level)
    {
        var item = await OverviewOf(
            new SensorGlucose { Mgdl = mgdl, Timestamp = DateTime.UtcNow.AddMinutes(-1) });

        item.Attention.Should().ContainSingle().Which.Should().Be(
            new TenantOverviewAttention(TenantOverviewAttentionKind.GlucoseOutOfRange, level));
    }

    [Theory]
    [InlineData(120)]
    [InlineData(200)]
    public async Task GetOverview_inRangeOrPlainHigh_buildsNoGlucoseReason(double mgdl)
    {
        var item = await OverviewOf(
            new SensorGlucose { Mgdl = mgdl, Timestamp = DateTime.UtcNow.AddMinutes(-1) });

        item.Attention.Should().BeEmpty();
    }

    [Fact]
    public async Task GetOverview_aStaleReading_buildsNoRecentReadingWithTheMinutesSince()
    {
        var item = await OverviewOf(
            new SensorGlucose { Mgdl = 120, Timestamp = DateTime.UtcNow.AddMinutes(-40) });

        item.Status.Should().Be(GlucoseStatus.Stale);
        var reason = item.Attention.Should().ContainSingle().Subject;
        reason.Kind.Should().Be(TenantOverviewAttentionKind.NoRecentReading);
        reason.Level.Should().Be(NotificationUrgency.Warn);
        reason.MinutesSinceReading.Should().BeInRange(40, 41);
    }

    [Fact]
    public async Task GetOverview_noReadingEver_buildsNoRecentReadingWithoutMinutes()
    {
        var item = await OverviewOf(latest: null);

        item.Attention.Should().ContainSingle().Which.Should().Be(
            new TenantOverviewAttention(TenantOverviewAttentionKind.NoRecentReading, NotificationUrgency.Warn));
    }

    [Theory]
    [InlineData(AlertRuleSeverity.Critical, NotificationUrgency.Urgent)]
    [InlineData(AlertRuleSeverity.Warning, NotificationUrgency.Warn)]
    [InlineData(AlertRuleSeverity.Info, NotificationUrgency.Info)]
    public void BuildAttention_anActiveAlert_takesItsLevelFromTheHighestSeverity(
        AlertRuleSeverity severity, NotificationUrgency level)
    {
        var reasons = TenantOverviewService.BuildAttention(
            GlucoseStatus.InRange, Reading(), lastReadingAt: null,
            activeAlertCount: 2, severity, trackers: null, DateTime.UtcNow);

        reasons.Should().ContainSingle().Which.Should().Be(
            new TenantOverviewAttention(TenantOverviewAttentionKind.ActiveAlert, level));
    }

    [Fact]
    public async Task GetOverview_anActiveAlertIsAReasonOnlyWithAlertsRead()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        var withScope = SeedMembership(options, subjectId, "with", [Scope.GlucoseRead, Scope.AlertsRead]);
        var withoutScope = SeedMembership(options, subjectId, "without", [Scope.GlucoseRead]);
        SeedActiveExcursion(options, withScope, AlertRuleSeverity.Critical, conditionParams: """{"direction":"above","value":400}""");
        SeedActiveExcursion(options, withoutScope, AlertRuleSeverity.Critical, conditionParams: """{"direction":"above","value":400}""");

        var latest = new SensorGlucose { Mgdl = 120, Timestamp = DateTime.UtcNow.AddMinutes(-1) };
        var response = await NewService(options, latest)
            .GetOverviewAsync(subjectId, FullTokenScopes, AuthType.OAuthAccessToken);

        response.Tenants.Single(t => t.TenantId == withScope).Attention.Should().ContainSingle().Which.Should().Be(
            new TenantOverviewAttention(TenantOverviewAttentionKind.ActiveAlert, NotificationUrgency.Urgent));
        response.Tenants.Single(t => t.TenantId == withoutScope).Attention.Should().BeEmpty();
    }

    [Fact]
    public async Task GetOverview_eachTrackerAtWarnOrAbove_buildsATrackerDueWithItsOwnUrgency()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        SeedMembership(options, subjectId, "family", [Scope.GlucoseRead, Scope.AlertsRead]);
        var fakes = new Fakes();
        fakes.Default = new TenantData
        {
            Latest = new SensorGlucose { Mgdl = 120, Timestamp = DateTime.UtcNow.AddMinutes(-1) },
            Trackers =
            [
                EventTracker("calm", hoursUntil: 10, NotificationUrgency.Info),
                EventTracker("site", hoursUntil: 1, NotificationUrgency.Warn),
                EventTracker("sensor", hoursUntil: 1, NotificationUrgency.Hazard),
            ],
        };

        var item = (await NewService(options, fakes: fakes)
            .GetOverviewAsync(subjectId, FullTokenScopes, AuthType.OAuthAccessToken)).Tenants.Single();

        item.Trackers.Should().HaveCount(3);
        item.Attention.Should().HaveCount(2);
        item.Attention.Select(r => (r.Kind, r.Level, r.TrackerName)).Should().Equal(
            (TenantOverviewAttentionKind.TrackerDue, NotificationUrgency.Hazard, "sensor"),
            (TenantOverviewAttentionKind.TrackerDue, NotificationUrgency.Warn, "site"));
        item.Attention.Should().OnlyContain(r => r.HoursUntilEvent >= 0.9 && r.HoursUntilEvent <= 1.0);
    }

    [Fact]
    public async Task GetOverview_reasonsAreOrderedMostSevereFirst()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        var tenantId = SeedMembership(options, subjectId, "family", [Scope.GlucoseRead, Scope.AlertsRead]);
        SeedActiveExcursion(options, tenantId, AlertRuleSeverity.Warning, conditionParams: """{"direction":"above","value":400}""");
        var fakes = new Fakes();
        fakes.Default = new TenantData
        {
            Latest = new SensorGlucose { Mgdl = 50, Timestamp = DateTime.UtcNow.AddMinutes(-1) },
        };

        var item = (await NewService(options, fakes: fakes)
            .GetOverviewAsync(subjectId, FullTokenScopes, AuthType.OAuthAccessToken)).Tenants.Single();

        item.Attention.Select(r => r.Kind).Should().Equal(
            TenantOverviewAttentionKind.GlucoseOutOfRange, TenantOverviewAttentionKind.ActiveAlert);
    }

    // ---- display order ----

    [Fact]
    public void CompareForDisplay_putsPeopleWithAReasonFirstBySeverityThenEveryoneByName()
    {
        var items = new List<TenantOverviewItem>
        {
            ItemOf("quiet-b", "Bea"),
            ItemOf("urgent-z", "Zoe", NotificationUrgency.Urgent),
            ItemOf("quiet-a", "ann"),
            ItemOf("warn-b", "Bob", NotificationUrgency.Info, NotificationUrgency.Warn),
            ItemOf("warn-a", "Abe", NotificationUrgency.Warn),
            ItemOf("urgent-a", "Amy", NotificationUrgency.Hazard, NotificationUrgency.Urgent),
            ItemOf("b", "Cal"),
            ItemOf("a", "cal"),
        };

        items.Sort(TenantOverviewService.CompareForDisplay);

        items.Select(i => i.Slug).Should().Equal(
            "urgent-a", "urgent-z", "warn-a", "warn-b", "quiet-a", "quiet-b", "a", "b");
    }

    [Fact]
    public async Task GetOverview_returnsItemsInDisplayOrder()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        var calm = SeedMembership(options, subjectId, "calm", [Scope.GlucoseRead]);
        var low = SeedMembership(options, subjectId, "low", [Scope.GlucoseRead]);
        var stale = SeedMembership(options, subjectId, "stale", [Scope.GlucoseRead]);
        var fakes = new Fakes();
        fakes.Data[calm] = new TenantData { Latest = new SensorGlucose { Mgdl = 120, Timestamp = DateTime.UtcNow.AddMinutes(-1) } };
        fakes.Data[low] = new TenantData { Latest = new SensorGlucose { Mgdl = 50, Timestamp = DateTime.UtcNow.AddMinutes(-1) } };
        fakes.Data[stale] = new TenantData { Latest = new SensorGlucose { Mgdl = 120, Timestamp = DateTime.UtcNow.AddMinutes(-90) } };

        var response = await NewService(options, fakes: fakes)
            .GetOverviewAsync(subjectId, FullTokenScopes, AuthType.OAuthAccessToken);

        response.Tenants.Select(t => t.Slug).Should().Equal("low", "stale", "calm");
    }

    private async Task<TenantOverviewItem> OverviewOf(SensorGlucose? latest)
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        SeedMembership(options, subjectId, "family", [Scope.GlucoseRead]);
        return (await NewService(options, latest)
            .GetOverviewAsync(subjectId, FullTokenScopes, AuthType.OAuthAccessToken)).Tenants.Single();
    }

    private static TenantOverviewReading Reading() =>
        new(120, null, null, null, DateTime.UtcNow.AddMinutes(-1));

    private static TenantOverviewItem ItemOf(
        string slug, string displayName, params NotificationUrgency[] levels) =>
        new(Guid.NewGuid(), slug, displayName, null, null, GlucoseStatus.InRange, Defaults, null, null,
            [], null, null, null,
            levels.Select(l => new TenantOverviewAttention(TenantOverviewAttentionKind.TrackerDue, l)).ToList());

    private static IobCobReading OnBoard(double iob, double cob, bool insulin = true, bool carbs = true) =>
        new(new IobResult { Iob = iob }, new CobResult { Cob = cob },
            new IobCobInputs([], [], carbs ? [new CarbIntake()] : []))
        {
            InsulinInWindow = insulin,
        };

    private static TrackerInstanceEntity EventTracker(string name, double hoursUntil, NotificationUrgency urgency)
    {
        var definition = new TrackerDefinitionEntity
        {
            Id = Guid.NewGuid(),
            Name = name,
            Mode = TrackerMode.Event,
            DashboardVisibility = DashboardVisibility.Always,
        };
        definition.NotificationThresholds.Add(new TrackerNotificationThresholdEntity
        {
            Id = Guid.NewGuid(),
            TrackerDefinitionId = definition.Id,
            Urgency = urgency,
            Hours = -24,
        });
        return new TrackerInstanceEntity
        {
            Id = Guid.NewGuid(),
            DefinitionId = definition.Id,
            Definition = definition,
            ScheduledAt = DateTime.UtcNow.AddHours(hoursUntil),
        };
    }

    /// <summary>What one tenant's scope reads. A null member falls back to the call's default.</summary>
    private sealed class TenantData
    {
        public SensorGlucose? Latest { get; init; }
        public IReadOnlyList<SensorGlucose>? Recent { get; init; }
        public IobCobReading? OnBoard { get; init; }
        public PumpSnapshot? Pump { get; init; }
        public TrackerInstanceEntity[]? Trackers { get; init; }
    }

    private sealed class Fakes
    {
        public Mock<ICanonicalGlucoseService> Canonical { get; } = new();
        public Mock<IIobCobReader> IobCob { get; } = new();
        public Mock<IPumpSnapshotRepository> Pumps { get; } = new();
        public Mock<ITrackerRepository> Trackers { get; } = new();
        public Dictionary<Guid, TenantData> Data { get; } = [];
        public TenantData Default { get; set; } = new();

        public TenantData For(Guid tenantId) => Data.GetValueOrDefault(tenantId) ?? Default;

        public void Register(IServiceCollection services, Func<Guid?> tenantId, SensorGlucose? latest, Guid? failingTenantId)
        {
            Canonical.Setup(s => s.GetLatestAsync(It.IsAny<CancellationToken>()))
                .Returns(() => tenantId() == failingTenantId
                    ? Task.FromException<SensorGlucose?>(new InvalidOperationException("boom"))
                    : Task.FromResult(For(tenantId()!.Value).Latest ?? latest));
            Canonical.Setup(s => s.GetRecentAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .Returns(() => Task.FromResult<IReadOnlyList<SensorGlucose>>(For(tenantId()!.Value).Recent ?? []));
            IobCob.Setup(r => r.ReadTotalAsync(It.IsAny<long>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .Returns(() => Task.FromResult(
                    For(tenantId()!.Value).OnBoard ?? OnBoard(0, 0, insulin: false, carbs: false)));
            Pumps.Setup(r => r.GetLatestAsync(It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
                .Returns(() => Task.FromResult(For(tenantId()!.Value).Pump));
            Trackers.Setup(r => r.GetActiveInstancesAsync(It.IsAny<CancellationToken>()))
                .Returns(() => Task.FromResult<TrackerInstanceEntity[]>(For(tenantId()!.Value).Trackers ?? []));

            services.AddScoped(_ => Canonical.Object);
            services.AddScoped(_ => IobCob.Object);
            services.AddScoped(_ => Pumps.Object);
            services.AddScoped(_ => Trackers.Object);
        }
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
        bool tenantActive = true,
        bool limitTo24Hours = false,
        DateTime? lastReadingAt = null)
    {
        using var db = new NocturneDbContext(options);
        var tenant = new TenantEntity
        {
            Id = Guid.NewGuid(),
            Slug = slug,
            DisplayName = slug,
            IsActive = tenantActive,
            LastReadingAt = lastReadingAt,
        };
        db.Tenants.Add(tenant);

        if (!db.Subjects.Any(s => s.Id == subjectId))
            db.Subjects.Add(new SubjectEntity { Id = subjectId, Name = "Member" });

        var member = new TenantMemberEntity
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            SubjectId = subjectId,
            DirectPermissions = directPermissions,
            LimitTo24Hours = limitTo24Hours,
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
        Guid? failingTenantId = null,
        Fakes? fakes = null)
    {
        TenantContext? current = null;

        var accessor = new Mock<ITenantAccessor>();
        accessor.Setup(a => a.SetTenant(It.IsAny<TenantContext>()))
            .Callback<TenantContext>(tc => current = tc);

        var services = new ServiceCollection();
        (fakes ?? new Fakes()).Register(services, () => current?.TenantId, latest, failingTenantId);
        services.AddScoped(_ => accessor.Object);
        services.AddScoped<ICategoryReadContext>(_ => new Nocturne.Infrastructure.Data.Services.CategoryReadContext());
        services.AddScoped<INocturneDbContextFactory>(_ => new ScopeContextFactory(options, () => current!.TenantId));
        services.AddScoped<IAlertRepository>(sp => new AlertRepository(sp.GetRequiredService<INocturneDbContextFactory>()));
        var provider = services.BuildServiceProvider();

        return new TenantOverviewService(
            new TenantMembershipRepository((new InMemoryContextFactory(options)).AsNocturneFactory()),
            provider.CreateScope().ServiceProvider,
            new GlucoseStatusClassifier(
                new ConfigurationBuilder().Build(), NullLogger<GlucoseStatusClassifier>.Instance),
            TimeProvider.System,
            NullLogger<TenantOverviewService>.Instance);
    }

    /// <summary>
    /// The per-tenant scope's context factory, pinned to that scope's tenant and recording the
    /// scope's clamp when a context is taken.
    /// </summary>
    private sealed class ScopeContextFactory(
        DbContextOptions<NocturneDbContext> options,
        Func<Guid> tenantId,
        ICategoryReadContext? category = null,
        Dictionary<Guid, bool>? clampSeen = null) : SingleReaderDbContextFactory
    {
        public override ValueTask<NocturneDbContext> CreateAsync(CancellationToken ct = default)
        {
            var context = new NocturneDbContext(options) { TenantId = tenantId() };
            if (clampSeen is not null)
                clampSeen[context.TenantId] = category?.IsHistoryClamped == true;
            return ValueTask.FromResult(context);
        }
    }

    private sealed class InMemoryContextFactory(DbContextOptions<NocturneDbContext> options)
        : IDbContextFactory<NocturneDbContext>
    {
        public NocturneDbContext CreateDbContext() => new(options);
    }
}
