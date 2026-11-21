using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Services.Glucose;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
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
public class TenantOverviewPersonTests
{
    private static readonly DateTime Now = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);

    private static readonly IReadOnlySet<string> FullTokenScopes =
        new HashSet<string> { Scope.FullAccess };

    private static readonly List<string> Reader = [Scope.GlucoseRead];
    private static readonly List<string> TreatmentReader = [Scope.GlucoseRead, Scope.TreatmentsRead];

    [Fact]
    public async Task A_tenant_the_subject_is_not_a_member_of_is_not_found_and_nothing_of_it_is_read()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        SeedMembership(options, subjectId, "mine", Reader);
        var strangers = SeedMembership(options, Guid.NewGuid(), "strangers", Reader);
        var harness = new Harness(options);

        var person = await harness.Service.GetPersonAsync(
            subjectId, strangers, FullTokenScopes, AuthType.SessionCookie);

        person.Should().BeNull();
        harness.Reads.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unknown_tenant_is_not_found_like_a_stranger_tenant()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        SeedMembership(options, subjectId, "mine", Reader);
        var harness = new Harness(options);

        var person = await harness.Service.GetPersonAsync(
            subjectId, Guid.NewGuid(), FullTokenScopes, AuthType.SessionCookie);

        person.Should().BeNull();
        harness.Reads.Should().BeEmpty();
    }

    [Fact]
    public async Task A_membership_without_glucose_read_is_not_found_and_nothing_of_it_is_read()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        var tenantId = SeedMembership(options, subjectId, "no-glucose", [Scope.TreatmentsRead]);
        var harness = new Harness(options);

        var person = await harness.Service.GetPersonAsync(
            subjectId, tenantId, FullTokenScopes, AuthType.SessionCookie);

        person.Should().BeNull();
        harness.Reads.Should().BeEmpty();
    }

    [Fact]
    public async Task A_caller_with_no_subject_is_not_found_and_nothing_is_read()
    {
        var options = NewOptions();
        var tenantId = SeedMembership(options, Guid.NewGuid(), "someone-else", Reader);
        var harness = new Harness(options);

        var person = await harness.Service.GetPersonAsync(
            null, tenantId, FullTokenScopes, AuthType.Guest);

        person.Should().BeNull();
        harness.Reads.Should().BeEmpty();
    }

    [Fact]
    public async Task The_person_is_the_item_the_overview_builds_for_the_tenant()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        var tenantId = SeedMembership(options, subjectId, "family", TreatmentReader);
        var harness = new Harness(options);
        harness.Data[tenantId] = new TenantData
        {
            Latest = new SensorGlucose { Mgdl = 55, Timestamp = Now.AddMinutes(-1) },
            Recent = Minutely(Now.AddHours(-3), Now),
        };

        var person = await harness.Service.GetPersonAsync(
            subjectId, tenantId, FullTokenScopes, AuthType.SessionCookie);
        var overview = await harness.Service.GetOverviewAsync(
            subjectId, FullTokenScopes, AuthType.SessionCookie);

        person!.Person.Should().BeEquivalentTo(overview.Tenants.Single());
        person.Person.Status.Should().Be(GlucoseStatus.Low);
    }

    [Fact]
    public async Task Readings_cover_the_whole_day_at_a_minute_cadence_oldest_first_one_per_bucket()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        var tenantId = SeedMembership(options, subjectId, "family", Reader);
        var harness = new Harness(options);
        var minutely = Minutely(Now.AddHours(-24), Now);
        minutely.Should().HaveCount(1440);
        harness.Data[tenantId] = new TenantData { Window = minutely };

        var person = (await harness.Service.GetPersonAsync(
            subjectId, tenantId, FullTokenScopes, AuthType.SessionCookie))!;

        var timestamps = person.Readings.Select(r => r.Timestamp).ToList();
        timestamps.Should().BeInAscendingOrder();
        timestamps.Select(t => t.Ticks / TimeSpan.FromMinutes(5).Ticks)
            .Should().OnlyHaveUniqueItems();
        timestamps.First().Should().BeBefore(person.From.AddMinutes(5));
        timestamps.Last().Should().BeOnOrAfter(Now.AddMinutes(-5));
        timestamps.Should().HaveCountGreaterThanOrEqualTo(288);
        person.To.Should().Be(Now);
        person.From.Should().Be(Now.AddHours(-24));
        harness.Bounds.Should().Contain(("window", Now.AddHours(-24), Now));
    }

    [Fact]
    public async Task Entries_are_null_without_the_treatments_scope_and_never_fetched()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        var tenantId = SeedMembership(options, subjectId, "family", Reader);
        var harness = new Harness(options);
        harness.Data[tenantId] = new TenantData
        {
            Boluses = [Bolus(Now.AddMinutes(-5), 2)],
            Intakes = [Intake(Now.AddMinutes(-6), 30)],
        };

        var person = (await harness.Service.GetPersonAsync(
            subjectId, tenantId, FullTokenScopes, AuthType.SessionCookie))!;

        person.Entries.Should().BeNull();
        harness.Reads.Should().NotContain(r => r.Kind == "boluses" || r.Kind == "intakes");
    }

    [Fact]
    public async Task A_delegated_token_without_the_treatments_scope_gets_no_entries()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        var tenantId = SeedMembership(options, subjectId, "family", [Scope.FullAccess]);
        var harness = new Harness(options);
        harness.Data[tenantId] = new TenantData { Boluses = [Bolus(Now.AddMinutes(-5), 2)] };

        var person = (await harness.Service.GetPersonAsync(
            subjectId, tenantId, new HashSet<string> { Scope.GlucoseRead }, AuthType.OAuthAccessToken))!;

        person.Entries.Should().BeNull();
        harness.Reads.Should().NotContain(r => r.Kind == "boluses" || r.Kind == "intakes");
    }

    [Fact]
    public async Task Entries_are_newest_first_and_capped_at_ten_across_both_kinds()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        var tenantId = SeedMembership(options, subjectId, "family", TreatmentReader);
        var harness = new Harness(options);
        harness.Data[tenantId] = new TenantData
        {
            Boluses = Enumerable.Range(0, 8).Select(i => Bolus(Now.AddMinutes(-10 * i - 1), 1 + i)).ToList(),
            Intakes = Enumerable.Range(0, 8).Select(i => Intake(Now.AddMinutes(-10 * i - 6), 10 + i)).ToList(),
        };

        var person = (await harness.Service.GetPersonAsync(
            subjectId, tenantId, FullTokenScopes, AuthType.SessionCookie))!;

        var entries = person.Entries!;
        entries.Should().HaveCount(10);
        entries.Select(e => e.Timestamp).Should().BeInDescendingOrder();
        entries.Select(e => e.Timestamp).Should().Equal(
            Enumerable.Range(0, 5).SelectMany(i => new[] { Now.AddMinutes(-10 * i - 1), Now.AddMinutes(-10 * i - 6) }));
        entries.Where(e => e.Kind == TenantPersonEntryKind.Bolus).Should()
            .OnlyContain(e => e.Units != null && e.Grams == null);
        entries.Where(e => e.Kind == TenantPersonEntryKind.Carbs).Should()
            .OnlyContain(e => e.Grams != null && e.Units == null);
        entries[0].Should().Be(new TenantPersonEntry(TenantPersonEntryKind.Bolus, Now.AddMinutes(-1), 1, null));
        entries[1].Should().Be(new TenantPersonEntry(TenantPersonEntryKind.Carbs, Now.AddMinutes(-6), null, 10));
    }

    [Fact]
    public async Task A_member_with_the_treatments_scope_and_no_entries_gets_an_empty_list()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        var tenantId = SeedMembership(options, subjectId, "family", TreatmentReader);
        var harness = new Harness(options);

        var person = (await harness.Service.GetPersonAsync(
            subjectId, tenantId, FullTokenScopes, AuthType.SessionCookie))!;

        person.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Entries_are_manual_boluses_and_carb_intakes_even_when_algorithm_boluses_are_newer()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        var tenantId = SeedMembership(options, subjectId, "family", TreatmentReader);
        var harness = new Harness(options);
        harness.Data[tenantId] = new TenantData
        {
            Boluses = Enumerable.Range(0, 12).Select(i => Bolus(Now.AddMinutes(-5 * i - 1), 0.1, BolusKind.Algorithm))
                .Append(Bolus(Now.AddHours(-2), 4))
                .ToList(),
            Intakes = [Intake(Now.AddHours(-3), 40)],
        };

        var person = (await harness.Service.GetPersonAsync(
            subjectId, tenantId, FullTokenScopes, AuthType.SessionCookie))!;

        person.Entries.Should().Equal(
            new TenantPersonEntry(TenantPersonEntryKind.Bolus, Now.AddHours(-2), 4, null),
            new TenantPersonEntry(TenantPersonEntryKind.Carbs, Now.AddHours(-3), null, 40));
    }

    [Fact]
    public async Task Readings_and_entries_dated_after_the_window_are_left_out()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        var tenantId = SeedMembership(options, subjectId, "family", TreatmentReader);
        var harness = new Harness(options);
        harness.Data[tenantId] = new TenantData
        {
            Window =
            [
                new SensorGlucose { Mgdl = 300, Timestamp = Now.AddMinutes(30) },
                new SensorGlucose { Mgdl = 100, Timestamp = Now.AddMinutes(-1) },
            ],
            Boluses = [Bolus(Now.AddMinutes(10), 9), Bolus(Now.AddMinutes(-2), 1)],
            Intakes = [Intake(Now.AddMinutes(20), 99), Intake(Now.AddMinutes(-3), 10)],
        };

        var person = (await harness.Service.GetPersonAsync(
            subjectId, tenantId, FullTokenScopes, AuthType.SessionCookie))!;

        person.Readings.Select(r => r.Mgdl).Should().Equal(100);
        person.Entries!.Select(e => e.Timestamp).Should().Equal(Now.AddMinutes(-2), Now.AddMinutes(-3));
    }

    [Fact]
    public async Task Entries_are_read_from_the_start_of_the_window()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        var tenantId = SeedMembership(options, subjectId, "family", TreatmentReader);
        var harness = new Harness(options);

        await harness.Service.GetPersonAsync(subjectId, tenantId, FullTokenScopes, AuthType.SessionCookie);

        harness.Bounds.Should().BeEquivalentTo(
            [("window", Now.AddHours(-24), Now), ("boluses", Now.AddHours(-24), Now), ("intakes", Now.AddHours(-24), Now)]);
    }

    [Fact]
    public async Task A_clamped_membership_is_read_under_the_clamp_and_gets_the_same_window()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        var clamped = SeedMembership(options, subjectId, "clamped", TreatmentReader, limitTo24Hours: true);
        var full = SeedMembership(options, subjectId, "full", TreatmentReader);
        var harness = new Harness(options);

        var person = (await harness.Service.GetPersonAsync(
            subjectId, clamped, FullTokenScopes, AuthType.SessionCookie))!;
        await harness.Service.GetPersonAsync(subjectId, full, FullTokenScopes, AuthType.SessionCookie);

        harness.ClampsOf(clamped).Should().BeEquivalentTo(
            [("window", true), ("boluses", true), ("intakes", true)]);
        harness.ClampsOf(full).Should().BeEquivalentTo(
            [("window", false), ("boluses", false), ("intakes", false)]);
        person.From.Should().Be(Now.AddHours(-24));
        harness.Bounds.Should().OnlyContain(b => b.From == Now.AddHours(-24));
    }

    [Fact]
    public async Task A_clamped_credential_is_read_under_the_clamp_even_for_an_owner()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        var owner = SeedMembership(options, subjectId, "owner", [Scope.FullAccess]);
        var harness = new Harness(options);

        await harness.Service.GetPersonAsync(
            subjectId, owner, FullTokenScopes, AuthType.OAuthAccessToken, credentialLimitTo24Hours: true);

        harness.ClampsOf(owner).Should().HaveCount(3).And.OnlyContain(c => c.Clamped);
    }

    [Fact]
    public async Task Two_tenants_never_mix()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        var a = SeedMembership(options, subjectId, "a", TreatmentReader);
        var b = SeedMembership(options, subjectId, "b", TreatmentReader);
        var harness = new Harness(options);
        harness.Data[a] = new TenantData
        {
            Window = [new SensorGlucose { Mgdl = 101, Timestamp = Now.AddMinutes(-2) }],
            Boluses = [Bolus(Now.AddMinutes(-3), 1.5)],
        };
        harness.Data[b] = new TenantData
        {
            Window = [new SensorGlucose { Mgdl = 202, Timestamp = Now.AddMinutes(-2) }],
            Intakes = [Intake(Now.AddMinutes(-4), 45)],
        };

        var personA = (await harness.Service.GetPersonAsync(subjectId, a, FullTokenScopes, AuthType.SessionCookie))!;
        var personB = (await harness.Service.GetPersonAsync(subjectId, b, FullTokenScopes, AuthType.SessionCookie))!;

        personA.Person.TenantId.Should().Be(a);
        personA.Readings.Select(r => r.Mgdl).Should().Equal(101);
        personA.Entries!.Select(e => e.Kind).Should().Equal(TenantPersonEntryKind.Bolus);
        personB.Person.TenantId.Should().Be(b);
        personB.Readings.Select(r => r.Mgdl).Should().Equal(202);
        personB.Entries!.Select(e => e.Kind).Should().Equal(TenantPersonEntryKind.Carbs);
    }

    [Fact]
    public async Task A_failing_read_is_not_swallowed()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        var tenantId = SeedMembership(options, subjectId, "bad", Reader);
        var harness = new Harness(options) { Failing = tenantId };

        var read = () => harness.Service.GetPersonAsync(
            subjectId, tenantId, FullTokenScopes, AuthType.SessionCookie);

        await read.Should().ThrowAsync<InvalidOperationException>();
    }

    private static List<SensorGlucose> Minutely(DateTime from, DateTime to)
    {
        var readings = new List<SensorGlucose>();
        for (var at = to; at > from; at = at.AddMinutes(-1))
            readings.Add(new SensorGlucose { Mgdl = 120, Timestamp = at });
        return readings;
    }

    private static Bolus Bolus(DateTime at, double units, BolusKind kind = BolusKind.Manual) =>
        new() { Timestamp = at, Insulin = units, Kind = kind };

    private static CarbIntake Intake(DateTime at, double grams) => new() { Timestamp = at, Carbs = grams };

    private sealed class TenantData
    {
        public SensorGlucose? Latest { get; init; }
        public IReadOnlyList<SensorGlucose>? Recent { get; init; }
        public IReadOnlyList<SensorGlucose>? Window { get; init; }
        public IReadOnlyList<Bolus>? Boluses { get; init; }
        public IReadOnlyList<CarbIntake>? Intakes { get; init; }
    }

    private sealed class PinRecordingTenantAccessor(List<(Guid Tenant, string Kind)> reads) : ITenantAccessor
    {
        public Guid TenantId => Context?.TenantId ?? Guid.Empty;
        public bool IsResolved => Context is not null;
        public TenantContext? Context { get; private set; }

        public void SetTenant(TenantContext context)
        {
            Context = context;
            reads.Add((context.TenantId, "scope"));
        }
    }

    private sealed class Harness
    {
        public Dictionary<Guid, TenantData> Data { get; } = [];
        public List<(Guid Tenant, string Kind)> Reads { get; } = [];
        public List<(Guid Tenant, string Kind, bool Clamped)> Clamps { get; } = [];
        public List<(string Kind, DateTime? From, DateTime? To)> Bounds { get; } = [];

        public IEnumerable<(string Kind, bool Clamped)> ClampsOf(Guid tenantId) =>
            Clamps.Where(c => c.Tenant == tenantId).Select(c => (c.Kind, c.Clamped));
        public Guid? Failing { get; init; }
        public TenantOverviewService Service { get; }

        public Harness(DbContextOptions<NocturneDbContext> options)
        {
            var services = new ServiceCollection();
            services.AddScoped<ITenantAccessor>(_ => new PinRecordingTenantAccessor(Reads));
            services.AddScoped<ICategoryReadContext>(_ => new CategoryReadContext());
            services.AddScoped<INocturneDbContextFactory>(sp => new ScopeContextFactory(
                options, () => sp.GetRequiredService<ITenantAccessor>().TenantId));
            services.AddScoped<IAlertRepository>(sp => new AlertRepository(sp.GetRequiredService<INocturneDbContextFactory>()));
            services.AddScoped(sp =>
            {
                var tenantId = sp.GetRequiredService<ITenantAccessor>().TenantId;
                var category = sp.GetRequiredService<ICategoryReadContext>();
                TenantData Of() => Data.GetValueOrDefault(tenantId) ?? new TenantData();
                var canonical = new Mock<ICanonicalGlucoseService>();
                canonical.Setup(s => s.GetLatestAsync(It.IsAny<CancellationToken>()))
                    .Returns(() => Task.FromResult(Of().Latest));
                canonical.Setup(s => s.GetRecentAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                    .Returns(() => Task.FromResult(Of().Recent ?? []));
                canonical.Setup(s => s.GetWindowAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                    .Returns((DateTime since, DateTime to, CancellationToken _) =>
                    {
                        Reads.Add((tenantId, "window"));
                        Bounds.Add(("window", since, to));
                        Clamps.Add((tenantId, "window", category.IsHistoryClamped));
                        return tenantId == Failing
                            ? Task.FromException<IReadOnlyList<SensorGlucose>>(new InvalidOperationException("boom"))
                            : Task.FromResult<IReadOnlyList<SensorGlucose>>(
                                (Of().Window ?? []).Where(r => r.Timestamp >= since && r.Timestamp <= to).ToList());
                    });
                return canonical.Object;
            });
            services.AddScoped(sp =>
            {
                var tenantId = sp.GetRequiredService<ITenantAccessor>().TenantId;
                var category = sp.GetRequiredService<ICategoryReadContext>();
                var boluses = new Mock<IBolusRepository>();
                boluses.Setup(r => r.GetAsync(
                        It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                        It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<BolusKind?>(),
                        It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                    .Returns((DateTime? from, DateTime? to, string? _, string? _, int limit, int _, bool _,
                        BolusKind? kind, DateTime? _, Guid? _, CancellationToken _) =>
                    {
                        Reads.Add((tenantId, "boluses"));
                        Bounds.Add(("boluses", from, to));
                        Clamps.Add((tenantId, "boluses", category.IsHistoryClamped));
                        return Task.FromResult<IEnumerable<Bolus>>(
                            (Data.GetValueOrDefault(tenantId)?.Boluses ?? [])
                                .Where(b => kind is null || b.Kind == kind)
                                .Where(b => b.Timestamp >= from && b.Timestamp <= to)
                                .OrderByDescending(b => b.Timestamp)
                                .Take(limit)
                                .ToList());
                    });
                return boluses.Object;
            });
            services.AddScoped(sp =>
            {
                var tenantId = sp.GetRequiredService<ITenantAccessor>().TenantId;
                var category = sp.GetRequiredService<ICategoryReadContext>();
                var intakes = new Mock<ICarbIntakeRepository>();
                intakes.Setup(r => r.GetAsync(
                        It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                        It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                        It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                    .Returns((DateTime? from, DateTime? to, string? _, string? _, int limit, int _, bool _,
                        DateTime? _, Guid? _, CancellationToken _) =>
                    {
                        Reads.Add((tenantId, "intakes"));
                        Bounds.Add(("intakes", from, to));
                        Clamps.Add((tenantId, "intakes", category.IsHistoryClamped));
                        return Task.FromResult<IEnumerable<CarbIntake>>(
                            (Data.GetValueOrDefault(tenantId)?.Intakes ?? [])
                                .Where(c => c.Timestamp >= from && c.Timestamp <= to)
                                .OrderByDescending(c => c.Timestamp)
                                .Take(limit)
                                .ToList());
                    });
                return intakes.Object;
            });
            var iobCob = new Mock<IIobCobReader>();
            iobCob.Setup(r => r.ReadTotalAsync(It.IsAny<long>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new IobCobReading(
                    new IobResult(), new CobResult(), new IobCobInputs([], [], [])) { InsulinInWindow = false });
            services.AddScoped(_ => iobCob.Object);
            var pumps = new Mock<IPumpSnapshotRepository>();
            pumps.Setup(r => r.GetLatestAsync(It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((PumpSnapshot?)null);
            services.AddScoped(_ => pumps.Object);
            var trackers = new Mock<ITrackerRepository>();
            trackers.Setup(r => r.GetActiveInstancesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<TrackerInstance>());
            services.AddScoped(_ => trackers.Object);
            var provider = services.BuildServiceProvider();

            Service = new TenantOverviewService(
                new TenantMembershipRepository(new InMemoryContextFactory(options).AsNocturneFactory(), AccountLinkRoutes.Off),
                provider.CreateScope().ServiceProvider,
                new GlucoseStatusClassifier(
                    new ConfigurationBuilder().Build(), NullLogger<GlucoseStatusClassifier>.Instance),
                new FakeTimeProvider(new DateTimeOffset(Now)),
                NullLogger<TenantOverviewService>.Instance);
        }
    }
}
