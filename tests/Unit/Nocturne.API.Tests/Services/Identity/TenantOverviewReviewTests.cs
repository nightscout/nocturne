using Nocturne.Core.Models.Identity;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Services.Analytics;
using Nocturne.API.Services.Caching;
using Nocturne.API.Services.Glucose;
using Nocturne.API.Services.Identity;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;
using static Nocturne.API.Tests.Services.Identity.TenantOverviewTestKit;

namespace Nocturne.API.Tests.Services.Identity;

[Trait("Category", "Unit")]
public partial class TenantOverviewReviewTests
{
    private static readonly DateTime Now = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);

    private static readonly IReadOnlySet<string> FullTokenScopes =
        new HashSet<string> { Scope.FullAccess };

    private static readonly List<string> ReportReader = [Scope.GlucoseRead, Scope.ReportsRead];

    [Fact]
    public async Task A_caller_with_no_subject_gets_the_periods_and_no_people()
    {
        var options = NewOptions();
        SeedMembership(options, Guid.NewGuid(), "someone-else", ReportReader);
        var harness = new Harness(options);

        var review = await harness.Service.GetReviewAsync(
            null, FullTokenScopes, AuthType.Guest, days: 14, OverviewRuleSet.Fixed, PeopleQuery.Empty);

        review.People.Should().BeEmpty();
        review.To.Should().Be(Now);
        review.From.Should().Be(Now.AddDays(-14));
        review.PreviousTo.Should().Be(review.From);
        review.PreviousFrom.Should().Be(Now.AddDays(-28));
        harness.Reads.Should().BeEmpty();
    }

    [Fact]
    public async Task A_member_without_reports_read_is_unavailable_and_nothing_is_read()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        var tenantId = SeedMembership(options, subjectId, "no-reports", [Scope.GlucoseRead]);
        var harness = new Harness(options);

        var review = await harness.Service.GetReviewAsync(
            subjectId, FullTokenScopes, AuthType.SessionCookie, days: 14, OverviewRuleSet.Fixed, PeopleQuery.Empty);

        var person = review.People.Should().ContainSingle().Subject;
        person.TenantId.Should().Be(tenantId);
        person.Unavailable.Should().Be(TenantReviewUnavailable.NoReportAccess);
        person.Statistics.Should().BeNull();
        harness.Reads.Should().BeEmpty();
    }

    [Fact]
    public async Task A_token_without_reports_read_is_unavailable_on_a_tenant_whose_membership_has_it()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        SeedMembership(options, subjectId, "owner", [Scope.FullAccess]);
        var harness = new Harness(options);

        var review = await harness.Service.GetReviewAsync(
            subjectId, new HashSet<string> { Scope.GlucoseRead }, AuthType.OAuthAccessToken, days: 14, OverviewRuleSet.Fixed, PeopleQuery.Empty);

        review.People.Should().ContainSingle().Which.Unavailable
            .Should().Be(TenantReviewUnavailable.NoReportAccess);
        harness.Reads.Should().BeEmpty();
    }

    [Fact]
    public async Task A_membership_limited_to_24_hours_is_history_limited_and_nothing_is_read()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        SeedMembership(options, subjectId, "limited", ReportReader, limitTo24Hours: true);
        var harness = new Harness(options);

        var review = await harness.Service.GetReviewAsync(
            subjectId, FullTokenScopes, AuthType.SessionCookie, days: 14, OverviewRuleSet.Fixed, PeopleQuery.Empty);

        var person = review.People.Should().ContainSingle().Subject;
        person.Unavailable.Should().Be(TenantReviewUnavailable.HistoryLimited);
        person.Statistics.Should().BeNull();
        harness.Reads.Should().BeEmpty();
    }

    [Fact]
    public async Task A_credential_limited_to_24_hours_is_history_limited_on_every_tenant_even_an_owned_one()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        SeedMembership(options, subjectId, "member", ReportReader);
        SeedMembership(options, subjectId, "owner", [Scope.FullAccess]);
        var harness = new Harness(options);

        var review = await harness.Service.GetReviewAsync(
            subjectId, FullTokenScopes, AuthType.OAuthAccessToken, days: 14, OverviewRuleSet.Fixed, PeopleQuery.Empty,
            credentialLimitTo24Hours: true);

        review.People.Should().HaveCount(2).And.OnlyContain(p =>
            p.Unavailable == TenantReviewUnavailable.HistoryLimited && p.Statistics == null);
        harness.Reads.Should().BeEmpty();
    }

    [Fact]
    public async Task The_change_is_this_period_less_the_previous_one_in_points()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        var tenantId = SeedMembership(options, subjectId, "family", ReportReader);
        var harness = new Harness(options);
        harness.Series[tenantId] = Harness.Period(current: 3, previous: 1);

        var review = await harness.Service.GetReviewAsync(
            subjectId, FullTokenScopes, AuthType.SessionCookie, days: 14, OverviewRuleSet.Fixed, PeopleQuery.Empty);

        var statistics = review.People.Single().Statistics!;
        statistics.TimeInRange!.Target.Should().BeApproximately(75, 2);
        statistics.TimeInRangeChange.Should().BeApproximately(50, 2);
    }

    [Fact]
    public async Task A_fall_in_time_in_range_is_a_negative_change()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        var tenantId = SeedMembership(options, subjectId, "family", ReportReader);
        var harness = new Harness(options);
        harness.Series[tenantId] = Harness.Period(current: 1, previous: 3);

        var review = await harness.Service.GetReviewAsync(
            subjectId, FullTokenScopes, AuthType.SessionCookie, days: 14, OverviewRuleSet.Fixed, PeopleQuery.Empty);

        review.People.Single().Statistics!.TimeInRangeChange.Should().BeApproximately(-50, 2);
    }

    [Fact]
    public async Task There_is_no_change_when_the_previous_period_is_not_sufficient()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        var tenantId = SeedMembership(options, subjectId, "family", ReportReader);
        var harness = new Harness(options);
        harness.Series[tenantId] = Harness.Period(current: 3, previous: 1, previousDaysOfData: 2);

        var review = await harness.Service.GetReviewAsync(
            subjectId, FullTokenScopes, AuthType.SessionCookie, days: 14, OverviewRuleSet.Fixed, PeopleQuery.Empty);

        var statistics = review.People.Single().Statistics!;
        statistics.TimeInRange.Should().NotBeNull();
        statistics.TimeInRangeChange.Should().BeNull();
    }

    [Fact]
    public async Task There_is_no_change_when_the_previous_period_has_no_data()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        var tenantId = SeedMembership(options, subjectId, "family", ReportReader);
        var harness = new Harness(options);
        harness.Series[tenantId] = Harness.Period(current: 3, previous: 1, previousDaysOfData: 0);

        var review = await harness.Service.GetReviewAsync(
            subjectId, FullTokenScopes, AuthType.SessionCookie, days: 14, OverviewRuleSet.Fixed, PeopleQuery.Empty);

        review.People.Single().Statistics!.TimeInRangeChange.Should().BeNull();
    }

    [Fact]
    public async Task A_period_without_readings_has_statistics_with_no_figures()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        SeedMembership(options, subjectId, "quiet", ReportReader);
        var harness = new Harness(options);

        var review = await harness.Service.GetReviewAsync(
            subjectId, FullTokenScopes, AuthType.SessionCookie, days: 14, OverviewRuleSet.Fixed, PeopleQuery.Empty);

        var person = review.People.Single();
        person.Unavailable.Should().BeNull();
        var statistics = person.Statistics.Should().NotBeNull().And.Subject.As<TenantReviewStatistics>();
        statistics.TimeInRange.Should().BeNull();
        statistics.Gmi.Should().BeNull();
        statistics.CoefficientOfVariation.Should().BeNull();
        statistics.CgmActivePercent.Should().BeNull();
        statistics.TimeInRangeChange.Should().BeNull();
    }

    [Fact]
    public async Task A_failing_tenant_is_failed_and_the_others_are_intact()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        var okTenant = SeedMembership(options, subjectId, "ok", ReportReader);
        var badTenant = SeedMembership(options, subjectId, "bad", ReportReader);
        var harness = new Harness(options) { Failing = badTenant };
        harness.Series[okTenant] = Harness.Period(current: 3, previous: 3);

        var review = await harness.Service.GetReviewAsync(
            subjectId, FullTokenScopes, AuthType.SessionCookie, days: 14, OverviewRuleSet.Fixed, PeopleQuery.Empty);

        review.People.Should().HaveCount(2);
        var bad = review.People.Single(p => p.TenantId == badTenant);
        bad.Unavailable.Should().Be(TenantReviewUnavailable.Failed);
        bad.Statistics.Should().BeNull();
        var ok = review.People.Single(p => p.TenantId == okTenant);
        ok.Unavailable.Should().BeNull();
        ok.Statistics!.TimeInRange!.Target.Should().BeApproximately(75, 2);
    }

    [Fact]
    public async Task Two_tenants_in_one_call_share_no_figure()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        var inRange = SeedMembership(options, subjectId, "in-range", ReportReader);
        var high = SeedMembership(options, subjectId, "high", ReportReader);
        var harness = new Harness(options);
        harness.Series[inRange] = Harness.Period(current: 4, previous: 4);
        harness.Series[high] = Harness.Period(current: 0, previous: 0);

        var review = await harness.Service.GetReviewAsync(
            subjectId, FullTokenScopes, AuthType.SessionCookie, days: 14, OverviewRuleSet.Fixed, PeopleQuery.Empty);

        var a = review.People.Single(p => p.TenantId == inRange).Statistics!;
        var b = review.People.Single(p => p.TenantId == high).Statistics!;
        a.TimeInRange!.Target.Should().BeApproximately(100, 1);
        b.TimeInRange!.Target.Should().BeApproximately(0, 1);
        a.Gmi.Should().BeLessThan(b.Gmi!.Value);
    }

    [Fact]
    public async Task The_previous_period_directly_precedes_the_period_with_the_same_length()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        var tenantId = SeedMembership(options, subjectId, "family", ReportReader);
        var harness = new Harness(options);

        var review = await harness.Service.GetReviewAsync(
            subjectId, FullTokenScopes, AuthType.SessionCookie, days: 30, OverviewRuleSet.Fixed, PeopleQuery.Empty);

        review.To.Should().Be(Now);
        review.From.Should().Be(Now.AddDays(-30));
        review.PreviousTo.Should().Be(review.From);
        review.PreviousFrom.Should().Be(review.From.AddDays(-30));
        harness.Reads.Should().BeEquivalentTo(
            [(tenantId, review.From, review.To), (tenantId, review.PreviousFrom, review.PreviousTo)]);
    }

    [Fact]
    public async Task A_person_read_twice_inside_the_cache_window_is_read_once()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        SeedMembership(options, subjectId, "family", ReportReader);
        var harness = new Harness(options);

        await harness.Service.GetReviewAsync(subjectId, FullTokenScopes, AuthType.SessionCookie, days: 14, OverviewRuleSet.Fixed, PeopleQuery.Empty);
        await harness.Service.GetReviewAsync(subjectId, FullTokenScopes, AuthType.SessionCookie, days: 14, OverviewRuleSet.Fixed, PeopleQuery.Empty);
        harness.Reads.Should().HaveCount(2);

        await harness.Service.GetReviewAsync(subjectId, FullTokenScopes, AuthType.SessionCookie, days: 30, OverviewRuleSet.Fixed, PeopleQuery.Empty);
        harness.Reads.Should().HaveCount(4, "a different period is a different key");
    }

    [Fact]
    public async Task A_groups_review_reads_only_the_people_of_the_group_the_caller_reads()
    {
        var subjectId = Guid.NewGuid();
        var group = Guid.NewGuid();
        var options = NewOptions();
        var inGroup = SeedMembership(options, subjectId, "in-group", ReportReader);
        var outside = SeedMembership(options, subjectId, "outside", ReportReader);
        var harness = new Harness(options);
        harness.Teams
            .Setup(t => t.GetPeopleOfStaffGroupAsync(group, subjectId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<Guid> { inGroup, Guid.NewGuid() });

        var review = await harness.Service.GetGroupReviewAsync(
            subjectId, group, FullTokenScopes, AuthType.SessionCookie, 14, OverviewRuleSet.Fixed, PeopleQuery.Empty, false, CancellationToken.None);

        review!.People.Select(p => p.TenantId).Should().Equal(inGroup);
        harness.Reads.Select(r => r.Tenant).Should().OnlyContain(t => t == inGroup).And.NotBeEmpty();
        (await harness.Service.GetReviewAsync(subjectId, FullTokenScopes, AuthType.SessionCookie, days: 14, OverviewRuleSet.Fixed, PeopleQuery.Empty))
            .People.Select(p => p.TenantId).Should().BeEquivalentTo([inGroup, outside]);
    }

    private sealed class Harness
    {
        private const int SamplesPerDay = 48;

        public Dictionary<Guid, Func<DateTime, DateTime, IReadOnlyList<SensorGlucose>>> Series { get; } = [];
        public List<(Guid Tenant, DateTime From, DateTime To)> Reads { get; } = [];
        public Guid? Failing { get; init; }
        public Mock<ITeamRepository> Teams { get; } = new();
        public TenantOverviewService Service { get; }

        public Harness(Microsoft.EntityFrameworkCore.DbContextOptions<NocturneDbContext> options, IViewerFlagRules? rules = null)
        {
            var local = new MemoryCache(new MemoryCacheOptions());
            var distributed = TestTenantCache.Distributed(local);
            var therapy = new Mock<ITherapySettingsResolver>();
            therapy.Setup(r => r.GetTimezoneAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync("UTC");
            var statistics = new StatisticsService(TimeProvider.System);

            var services = new ServiceCollection();
            services.AddScoped<ITenantAccessor>(_ => new SettableTenantAccessor());
            services.AddScoped<ICategoryReadContext>(_ => new CategoryReadContext());
            services.AddScoped<ScopeConnection>();
            services.AddScoped<INocturneDbContextFactory>(sp =>
                new ScopeContextFactory(options, () => sp.GetRequiredService<ITenantAccessor>().TenantId));
            services.AddScoped<IAlertRepository>(sp => new AlertRepository(sp.GetRequiredService<INocturneDbContextFactory>()));
            services.AddScoped(_ =>
            {
                var canonical = new Mock<ICanonicalGlucoseService>();
                canonical.Setup(c => c.GetLatestAsync(It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new SensorGlucose { Mgdl = 120, Timestamp = Now.AddMinutes(-1) });
                canonical.Setup(c => c.GetRecentAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
                return canonical.Object;
            });
            services.AddScoped(sp => TestTenantCache.Create(
                local, distributed, sp.GetRequiredService<ITenantAccessor>().TenantId));
            services.AddScoped(sp =>
            {
                var tenantId = sp.GetRequiredService<ITenantAccessor>().TenantId;
                var glucose = new Mock<ISensorGlucoseRepository>();
                glucose
                    .Setup(r => r.GetForAnalyticsAsync(
                        It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                        It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<DateTime?>(),
                        It.IsAny<Guid?>(), It.IsAny<CancellationToken>(), It.IsAny<Guid?>(),
                        It.IsAny<GlucoseStreams>()))
                    .Returns((DateTime? from, DateTime? to, string? _, string? _, int _, int _, bool _,
                        DateTime? _, Guid? _, CancellationToken _, Guid? _, GlucoseStreams _) =>
                    {
                        Reads.Add((tenantId, from!.Value, to!.Value));
                        if (tenantId == Failing)
                            return Task.FromException<IEnumerable<SensorGlucose>>(new InvalidOperationException("boom"));
                        var readings = Series.TryGetValue(tenantId, out var series)
                            ? series(from.Value, to.Value)
                            : [];
                        return Task.FromResult<IEnumerable<SensorGlucose>>(readings);
                    });
                glucose
                    .Setup(r => r.CountByPatientDeviceAsync(
                        It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync([]);

                return new RangeAnalyticsReader(
                    glucose.Object,
                    Mock.Of<IBolusRepository>(),
                    Mock.Of<ICarbIntakeRepository>(),
                    Mock.Of<IPatientDeviceRepository>(),
                    statistics,
                    new TenantClockResolver(
                        therapy.Object, sp.GetRequiredService<ICategoryReadContext>(),
                        new ShareClockDouble(therapy.Object), Mock.Of<ILogger<TenantClockResolver>>()),
                    Mock.Of<IActiveProfileResolver>(),
                    Mock.Of<ITargetRangeScheduleRepository>(),
                    new InsulinDeliveryReader(
                        Mock.Of<IBolusRepository>(),
                        Mock.Of<ICarbIntakeRepository>(),
                        Mock.Of<ITempBasalRepository>(),
                        Mock.Of<IBasalInjectionRepository>(),
                        Mock.Of<IStateSpanRepository>(),
                        Mock.Of<IBasalSegmentService>(),
                        therapy.Object,
                        statistics,
                        TimeProvider.System),
                    TimeProvider.System);
            });
            var provider = services.BuildServiceProvider();

            Service = new TenantOverviewService(
                new TenantMembershipRepository(TestDbContextFactory.ForOptions(options), AccountLinkRoutes.Off),
                Teams.Object,
                provider.CreateScope().ServiceProvider,
                new GlucoseStatusClassifier(
                    new ConfigurationBuilder().Build(), NullLogger<GlucoseStatusClassifier>.Instance),
                rules ?? Mock.Of<IViewerFlagRules>(),
                new FakeTimeProvider(new DateTimeOffset(Now)),
                NullLogger<TenantOverviewService>.Instance);
        }

        /// <summary>
        /// Half-hourly readings, of which <paramref name="inRangeOfFour"/> in every four are in range
        /// and the rest high. The window ending now is the current period; the one before it is the
        /// previous. <paramref name="previousDaysOfData"/> keeps only the last days of the previous
        /// window.
        /// </summary>
        public static Func<DateTime, DateTime, IReadOnlyList<SensorGlucose>> Period(
            int current, int previous, int? previousDaysOfData = null) =>
            (from, to) => to == Now
                ? Readings(from, to, current)
                : Readings(previousDaysOfData is { } days ? to.AddDays(-days) : from, to, previous);

        private static List<SensorGlucose> Readings(DateTime from, DateTime to, int inRangeOfFour)
        {
            var step = TimeSpan.FromDays(1) / SamplesPerDay;
            var readings = new List<SensorGlucose>();
            for (var (at, i) = (from, 0); at < to; at += step, i++)
                readings.Add(new SensorGlucose { Mgdl = i % 4 < inRangeOfFour ? 120 : 250, Timestamp = at });
            return readings;
        }
    }
}
