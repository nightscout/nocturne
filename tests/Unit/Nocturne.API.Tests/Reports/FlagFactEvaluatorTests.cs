using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Filters;
using Nocturne.API.Reports;
using Nocturne.API.Services.Analytics;
using Nocturne.API.Services.Caching;
using Nocturne.API.Services.Platform;
using Nocturne.API.Services.Identity;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Tests.Shared.Logging;
using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;
using static Nocturne.API.Tests.Services.Identity.TenantOverviewTestKit;

namespace Nocturne.API.Tests.Reports;

/// <summary>
/// The flag fact evaluator across many people: each outcome, the scopes a fact needs, the own range, the keep of
/// figures and its background reads, the read audit and the batch, and that its figures are the ones a custom report's
/// figures panel gives for the same person and range.
/// </summary>
[Trait("Category", "Unit")]
public partial class FlagFactEvaluatorTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Start = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid Viewer = Guid.NewGuid();

    private static readonly IReadOnlySet<string> Reports = new HashSet<string> { Scope.GlucoseRead, Scope.ReportsRead };

    private static readonly IReadOnlySet<string> Therapy = new HashSet<string> { Scope.GlucoseRead, Scope.ReportsRead, Scope.TherapyRead };

    /// <summary>A reading every five minutes back from <see cref="Start"/>: one in ten at 60 mg/dL, the rest 100 to 160.</summary>
    private static double Pattern(int i) => i % 10 == 0 ? 60 : 100 + i % 7 * 10;

    /// <summary>A target range schedule of one entry all day.</summary>
    private static (string Time, double Low, double High)[] AllDay(double low, double high) => [("00:00", low, high)];

    /// <summary>
    /// One person's data: readings back over <paramref name="History"/>, their own target range schedule, and the latest
    /// reading's age. With <paramref name="ReadFailsAfter"/>, reading their glucose waits for it, then fails as the
    /// database fails; with <paramref name="Defect"/>, it throws that at once. <paramref name="Pumps"/> are their pump
    /// snapshots, oldest first. <paramref name="OnBoard"/> is what the overview read of their insulin and carbs on board.
    /// </summary>
    private sealed record Person(
        Func<int, double> Mgdl,
        TimeSpan History,
        (string Time, double Low, double High)[]? Schedule = null,
        TimeSpan? LatestAge = null,
        Task? ReadFailsAfter = null,
        Exception? Defect = null,
        PumpSnapshot[]? Pumps = null,
        OnBoardFigures? OnBoard = null);

    private sealed class Harness
    {
        private int _glucoseReads;
        private int _pumpReads;
        private int _latestReads;
        private int _scheduleReads;
        private int _roundTrips;
        private int _glucoseReadsAtOnce;
        private int _mostGlucoseReadsAtOnce;
        private readonly ReadAccessLogWriter _writer;
        private readonly List<ReadAccessLogEntity> _rows = [];

        public FakeTimeProvider Time { get; } = new(Start);
        public Dictionary<Guid, Person> Data { get; } = [];
        public IServiceProvider Request { get; }
        public FlagFactReads Reads { get; }
        public FlagFactEvaluator Evaluator { get; }
        public ReportSpecValidator Validator { get; }

        public int GlucoseReads => _glucoseReads;

        /// <summary>Reads of pump snapshots over a range, as the device status dataset reads them.</summary>
        public int PumpReads => _pumpReads;
        public int LatestReads => _latestReads;
        public int ScheduleReads => _scheduleReads;

        /// <summary>Every repository call the datasets made: in production each is one database round trip.</summary>
        public int RoundTrips => _roundTrips;

        /// <summary>When set, reading a target range schedule fails as the database fails.</summary>
        public bool ScheduleFails { get; set; }

        /// <summary>When set, reading a target range schedule throws this, which is no failure of the database.</summary>
        public Exception? ScheduleDefect { get; set; }

        /// <summary>Whether the people audit reads.</summary>
        public bool ReadAuditEnabled { get; set; } = true;

        /// <summary>When set, reading the read-audit setting fails as the database fails.</summary>
        public bool AuditSettingFails { get; set; }

        /// <summary>Runs as each read-audit setting is read, after its token is checked.</summary>
        public Action? OnAuditSettingRead { get; set; }

        /// <summary>Runs as each latest reading is read.</summary>
        public Action? OnLatestRead { get; set; }

        public ListLogger<FlagFactReads> Log { get; } = new();

        /// <summary>When set, each glucose read waits on it before it answers.</summary>
        public Func<Task>? DuringGlucoseRead { get; set; }

        /// <summary>The most glucose reads that were running at one time.</summary>
        public int MostGlucoseReadsAtOnce => Volatile.Read(ref _mostGlucoseReadsAtOnce);

        /// <summary>The glucose reads running now.</summary>
        public int GlucoseReadsAtOnce => Volatile.Read(ref _glucoseReadsAtOnce);

        /// <summary>When set, reading this person's pump snapshots fails as the database fails.</summary>
        public Guid? PumpFailsFor { get; set; }

        public Harness(int limit = 0)
        {
            var local = new MemoryCache(new MemoryCacheOptions());
            var distributed = TestTenantCache.Distributed(local);
            var therapy = new Mock<ITherapySettingsResolver>();
            therapy.Setup(t => t.GetTimezoneAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .Callback(() => Interlocked.Increment(ref _roundTrips)).ReturnsAsync("UTC");
            var auditConfig = new Mock<ITenantAuditConfigCache>();
            auditConfig.Setup(c => c.GetConfigAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Returns((Guid _, CancellationToken ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    OnAuditSettingRead?.Invoke();
                    return AuditSettingFails
                        ? throw new NpgsqlException("connection refused")
                        : Task.FromResult(new TenantAuditConfig(ReadAuditEnabled, null, null));
                });
            var boluses = new Mock<IBolusRepository>();
            boluses.Setup(b => b.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null, It.IsAny<int>(), It.IsAny<int>(),
                    It.IsAny<bool>(), It.IsAny<BolusKind?>(), It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((DateTime? _, DateTime? _, string? _, string? _, int _, int _, bool _, BolusKind? kind, DateTime? _, Guid? _, CancellationToken _) =>
                    kind == BolusKind.Algorithm ? [] : [new Bolus { Timestamp = Start.UtcDateTime.AddDays(-1), Insulin = 28 }]);
            _writer = new ReadAccessLogWriter(Rows().AsNocturneFactory(), Time, NullLogger<ReadAccessLogWriter>.Instance);

            var services = new ServiceCollection();
            services.AddScoped<ITenantAccessor>(_ => new SettableTenantAccessor());
            services.AddScoped<ICategoryReadContext>(_ => new CategoryReadContext());
            var flights = new CacheFlights();
            var partition = Mock.Of<IReaderCachePartition>(p => p.Current == "full");
            var live = Mock.Of<IDemoModeService>(d => d.IsEnabled == false);
            services.AddScoped<ITenantCache>(sp => new TenantCache(distributed, local, flights, sp.GetRequiredService<ITenantAccessor>(), partition, live));
            services.AddSingleton(new TenantClockResolver(therapy.Object, new CategoryReadContext(), new ShareClockDouble(therapy.Object),
                NullLogger<TenantClockResolver>.Instance));
            services.AddSingleton<IStatisticsService>(new StatisticsService(Time));
            services.AddSingleton<TimeProvider>(Time);
            services.AddSingleton(auditConfig.Object);
            services.AddSingleton(_writer);
            services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
            services.AddScoped(sp => Pumps(sp.GetRequiredService<ITenantAccessor>().TenantId));
            services.AddSingleton(Mock.Of<IUploaderSnapshotRepository>());
            services.AddSingleton(Mock.Of<IDeviceAgeService>(d =>
                d.GetSensorAgeAsync(It.IsAny<DeviceAgePreferences>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()) == Task.FromResult(new SensorAgeInfo())));
            services.AddSingleton(Mock.Of<ITrackerRepository>());
            services.AddSingleton(boluses.Object);
            services.AddSingleton(new Mock<ITempBasalRepository>().Object);
            services.AddSingleton(new Mock<IBasalInjectionRepository>().Object);
            services.AddSingleton(new Mock<ICarbIntakeRepository>().Object);
            services.AddSingleton(Profiles());
            services.AddScoped(sp => DoublesOf(sp).Glucose);
            services.AddScoped(sp => DoublesOf(sp).Latest);
            services.AddScoped(sp => DoublesOf(sp).TargetRanges);

            var root = services.BuildServiceProvider();
            Request = root.CreateScope().ServiceProvider;
            Reads = new FlagFactReads(root.GetRequiredService<IServiceScopeFactory>(), Time, Log,
                limit > 0 ? limit : Environment.ProcessorCount);
            Validator = new ReportSpecValidator(ReportSpecValidatorTests.Registry, Time);
            Evaluator = For(Viewer);
        }

        /// <summary>The evaluator a request of <paramref name="viewer"/> resolves.</summary>
        public FlagFactEvaluator For(Guid viewer)
        {
            var evaluator = new FlagFactEvaluator(
                ReportSpecValidatorTests.Registry, Validator, Reads, Request,
                Mock.Of<IAuditContext>(a => a.SubjectId == viewer && a.SubjectName == "Viewer" && a.AuthType == "Session"),
                new HttpContextAccessor(), Time);
            Owners.Add(evaluator, this);
            return evaluator;
        }

        /// <summary>The read-audit rows written so far; the writer takes no more after.</summary>
        public async Task<IReadOnlyList<ReadAccessLogEntity>> AuditRowsAsync()
        {
            _writer.Complete();
            await _writer.DrainAsync();
            return _rows;
        }

        private IDbContextFactory<NocturneDbContext> Rows()
        {
            var contexts = new Mock<IDbContextFactory<NocturneDbContext>>();
            contexts.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() =>
            {
                var context = new Mock<NocturneDbContext>(
                    new DbContextOptionsBuilder<NocturneDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options) { CallBase = true };
                context.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()))
                    .Callback(() => _rows.AddRange(context.Object.ChangeTracker.Entries<ReadAccessLogEntity>().Select(e => e.Entity)))
                    .ReturnsAsync(1);
                return context.Object;
            });
            return contexts.Object;
        }

        /// <summary>A person's repositories, built once so the batch's time is the evaluator's and not the mocking library's.</summary>
        private sealed record Doubles(ISensorGlucoseRepository Glucose, ICanonicalGlucoseService Latest, ITargetRangeScheduleRepository TargetRanges);

        private readonly Dictionary<Guid, Doubles> _doubles = [];

        private Doubles DoublesOf(IServiceProvider scope)
        {
            var tenant = scope.GetRequiredService<ITenantAccessor>().TenantId;
            if (!_doubles.TryGetValue(tenant, out var doubles))
                _doubles[tenant] = doubles = new Doubles(Glucose(tenant), Latest(tenant), TargetRanges(tenant));
            return doubles;
        }

        private IReadOnlyList<SensorGlucose> Readings(Guid tenant, DateTime from, DateTime to)
        {
            if (!Data.TryGetValue(tenant, out var person))
                return [];
            var count = (int)(person.History / TimeSpan.FromMinutes(5));
            return [.. Enumerable.Range(0, count)
                .Select(i => new SensorGlucose { Timestamp = Start.UtcDateTime.AddMinutes(-5 * i), Mgdl = person.Mgdl(i) })
                .Where(r => r.Timestamp >= from && r.Timestamp < to)
                .OrderBy(r => r.Timestamp)];
        }

        private ISensorGlucoseRepository Glucose(Guid tenant)
        {
            var glucose = new Mock<ISensorGlucoseRepository>();
            glucose.Setup(g => g.GetForAnalyticsAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null, It.IsAny<int>(), It.IsAny<int>(),
                    It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>(), It.IsAny<Guid?>(), It.IsAny<GlucoseStreams>()))
                .Returns((DateTime? from, DateTime? to, string? _, string? _, int _, int _, bool _, DateTime? _, Guid? _, CancellationToken _, Guid? _, GlucoseStreams _) =>
                {
                    Interlocked.Increment(ref _glucoseReads);
                    Interlocked.Increment(ref _roundTrips);
                    Data.TryGetValue(tenant, out var person);
                    if (person?.Defect is { } defect)
                        return Task.FromException<IEnumerable<SensorGlucose>>(defect);
                    if (person?.ReadFailsAfter is { } gate)
                        return FailAfter(gate);
                    return DuringGlucoseRead is { } during
                        ? Counted(during, () => Readings(tenant, from!.Value, to!.Value))
                        : Task.FromResult<IEnumerable<SensorGlucose>>(Readings(tenant, from!.Value, to!.Value));
                });
            return glucose.Object;
        }

        private async Task<IEnumerable<SensorGlucose>> Counted(Func<Task> during, Func<IReadOnlyList<SensorGlucose>> readings)
        {
            var atOnce = Interlocked.Increment(ref _glucoseReadsAtOnce);
            int most;
            while (atOnce > (most = Volatile.Read(ref _mostGlucoseReadsAtOnce))
                && Interlocked.CompareExchange(ref _mostGlucoseReadsAtOnce, atOnce, most) != most)
            {
            }
            try
            {
                await during();
                return readings();
            }
            finally
            {
                Interlocked.Decrement(ref _glucoseReadsAtOnce);
            }
        }

        private IPumpSnapshotRepository Pumps(Guid tenant)
        {
            PumpSnapshot[] Held() => Data.TryGetValue(tenant, out var person) ? person.Pumps ?? [] : [];
            var pumps = new Mock<IPumpSnapshotRepository>();
            pumps.Setup(p => p.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                    It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .Returns((DateTime? from, DateTime? to, string? _, string? _, int _, int _, bool _, CancellationToken _) =>
                {
                    Interlocked.Increment(ref _pumpReads);
                    Interlocked.Increment(ref _roundTrips);
                    return tenant == PumpFailsFor
                        ? Task.FromException<IEnumerable<PumpSnapshot>>(new NpgsqlException("connection refused"))
                        : Task.FromResult<IEnumerable<PumpSnapshot>>([.. Held().Where(p => p.Timestamp >= from && p.Timestamp <= to)]);
                });
            pumps.Setup(p => p.GetLatestAsync(null, It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    Interlocked.Increment(ref _roundTrips);
                    return Task.FromResult(Held().LastOrDefault());
                });
            return pumps.Object;
        }

        private static async Task<IEnumerable<SensorGlucose>> FailAfter(Task gate)
        {
            await gate;
            throw new NpgsqlException("connection refused");
        }

        private ICanonicalGlucoseService Latest(Guid tenant)
        {
            var canonical = new Mock<ICanonicalGlucoseService>();
            canonical.Setup(c => c.GetLatestAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() =>
            {
                Interlocked.Increment(ref _latestReads);
                Interlocked.Increment(ref _roundTrips);
                OnLatestRead?.Invoke();
                return Data.TryGetValue(tenant, out var person) && person.LatestAge is { } age
                    ? new SensorGlucose { Timestamp = Start.UtcDateTime - age, Mgdl = 100 }
                    : null;
            });
            return canonical.Object;
        }

        private IActiveProfileResolver Profiles()
        {
            var profiles = new Mock<IActiveProfileResolver>();
            profiles.Setup(p => p.GetProfileNameAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
                .Callback(() => Interlocked.Increment(ref _roundTrips)).ReturnsAsync("Default");
            return profiles.Object;
        }

        private ITargetRangeScheduleRepository TargetRanges(Guid tenant)
        {
            var ranges = new Mock<ITargetRangeScheduleRepository>();
            ranges.Setup(r => r.GetActiveAtAsync("Default", It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync(() =>
            {
                Interlocked.Increment(ref _scheduleReads);
                Interlocked.Increment(ref _roundTrips);
                if (ScheduleFails)
                    throw new NpgsqlException("connection refused");
                if (ScheduleDefect is { } defect)
                    throw defect;
                return Data.TryGetValue(tenant, out var person) && person.Schedule is { } schedule
                    ? new TargetRangeSchedule
                    {
                        ProfileName = "Default",
                        Entries = [.. schedule.Select(e => new TargetRangeEntry { Time = e.Time, Low = e.Low, High = e.High })],
                    }
                    : null;
            });
            ranges.Setup(r => r.GetByProfileNameAsync("Default", It.IsAny<CancellationToken>()))
                .Callback(() => Interlocked.Increment(ref _roundTrips)).ReturnsAsync([]);
            return ranges.Object;
        }

        public GlucoseReadTenant Follow(Person person, IReadOnlySet<string>? scopes = null, bool limitTo24Hours = false)
        {
            var id = Guid.NewGuid();
            Data[id] = person;
            _doubles[id] = new Doubles(Glucose(id), Latest(id), TargetRanges(id));
            var tenant = new TenantRecord(id, $"p-{id:N}"[..10], "Person", true, false, false, false, null, null, null, null, null, null,
                null, null, null, null, null, DateTime.UnixEpoch, DateTime.UnixEpoch, null);
            var granted = scopes ?? Reports;
            return new GlucoseReadTenant(tenant, granted, limitTo24Hours, granted);
        }

        public FlagFactCondition Condition(string dataset, string field, int? days, FlagComparison comparison, double value, string? unit)
        {
            var (condition, errors) = Evaluator.Check(new FlagCondition(
                JsonDocument.Parse($$"""{ "name": "{{dataset}}" }""").RootElement, field, days, comparison, value, unit));
            errors.Should().BeEmpty();
            return condition!;
        }

        public async Task<IReadOnlyList<FlagJudgement>> JudgeAsync(GlucoseReadTenant person, params FlagFactCondition[] conditions) =>
            await JudgeAsync(Evaluator, person, conditions);

        public static async Task<IReadOnlyList<FlagJudgement>> JudgeAsync(
            FlagFactEvaluator evaluator, GlucoseReadTenant person, params FlagFactCondition[] conditions) =>
            (await JudgeManyAsync(evaluator, [person], conditions, false, CancellationToken.None)).Single();

        /// <summary>
        /// One pass as the People overview makes it: each person in their own scope, judged on the latest reading that
        /// scope reads, then the background reads started. Nothing waits for them.
        /// </summary>
        public static async Task<(IReadOnlyList<FlagPersonJudging> People, int Pending)> JudgeOnceAsync(
            FlagFactEvaluator evaluator,
            IReadOnlyList<GlucoseReadTenant> people,
            IReadOnlyList<FlagFactCondition> conditions,
            bool credentialLimitTo24Hours,
            CancellationToken cancellationToken)
        {
            var judging = evaluator.Begin(conditions, credentialLimitTo24Hours);
            var judged = new List<FlagPersonJudging>();
            var live = conditions.Any(c => c.Dataset.Name == "current_status");
            var devices = conditions.Any(c => c.Dataset.Name == "device_status");
            foreach (var person in people)
            {
                var clamped = MemberScopeResolver.IsHistoryClamped(
                    credentialLimitTo24Hours, person.MembershipLimitTo24Hours, person.EffectivePermissions);
                await using var scope = TenantOverviewService.OpenTenantScope(TheRequest(evaluator), person.Tenant, clamped, "test");
                var latest = live && !clamped
                    ? await scope.ServiceProvider.GetRequiredService<ICanonicalGlucoseService>().GetLatestAsync(cancellationToken)
                    : null;
                var pump = devices ? await NewestPumpAsync(person, scope.ServiceProvider, cancellationToken) : null;
                var onBoard = Scope.Satisfies(person.AllowedScopes, Scope.TreatmentsRead)
                    ? Owners.TryGetValue(evaluator, out var owner) ? owner.Data[person.Tenant.Id].OnBoard : null
                    : null;
                judged.Add(await judging.JudgeAsync(
                    person, scope.ServiceProvider, new FlagLiveReads(latest, [], pump, onBoard), [.. conditions.Select((_, i) => i)],
                    cancellationToken));
            }
            judging.Finish();
            return (judged, judging.Pending);
        }

        /// <summary>
        /// Every person's judgements as the overview gives them once its background reads are done: a pass, the reads it
        /// left, and a second pass for the people that were pending.
        /// </summary>
        public static async Task<IReadOnlyList<IReadOnlyList<FlagJudgement>>> JudgeManyAsync(
            FlagFactEvaluator evaluator,
            IReadOnlyList<GlucoseReadTenant> people,
            IReadOnlyList<FlagFactCondition> conditions,
            bool credentialLimitTo24Hours,
            CancellationToken cancellationToken)
        {
            var (first, _) = await JudgeOnceAsync(evaluator, people, conditions, credentialLimitTo24Hours, cancellationToken);
            await ReadsOf(evaluator).WhenIdleAsync();
            var result = new IReadOnlyList<FlagJudgement>[people.Count];
            var again = new List<int>();
            for (var i = 0; i < people.Count; i++)
            {
                if (first[i].State == FlagPersonState.Judged)
                    result[i] = first[i].Judgements;
                else
                    again.Add(i);
            }
            if (again.Count > 0)
            {
                var (second, _) = await JudgeOnceAsync(
                    evaluator, [.. again.Select(i => people[i])], conditions, credentialLimitTo24Hours, cancellationToken);
                for (var k = 0; k < again.Count; k++)
                {
                    second[k].State.Should().Be(FlagPersonState.Judged, "a person is judged once their background read is kept");
                    result[again[k]] = second[k].Judgements;
                }
            }
            return result;
        }

        /// <summary>The newest pump snapshot, read as the overview reads it for a caller who may read devices.</summary>
        public static async Task<PumpSnapshot?> NewestPumpAsync(GlucoseReadTenant person, IServiceProvider scope, CancellationToken cancellationToken) =>
            Scope.Satisfies(person.AllowedScopes, Scope.DevicesRead)
                ? await scope.GetRequiredService<IPumpSnapshotRepository>().GetLatestAsync(null, cancellationToken)
                : null;

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FlagFactEvaluator, Harness> Owners = new();

        private static IServiceProvider TheRequest(FlagFactEvaluator evaluator) =>
            Owners.TryGetValue(evaluator, out var harness) ? harness.Request : throw new InvalidOperationException("Not a harness evaluator.");

        private static FlagFactReads ReadsOf(FlagFactEvaluator evaluator) =>
            Owners.TryGetValue(evaluator, out var harness) ? harness.Reads : throw new InvalidOperationException("Not a harness evaluator.");

        /// <summary>The figures a custom report's figures panel gives for <paramref name="person"/> over the last <paramref name="hours"/>.</summary>
        public async Task<IReadOnlyDictionary<string, object?>> ReportAsync(GlucoseReadTenant person, string dataset, int hours)
        {
            var spec = Validator.Validate(JsonDocument.Parse($$"""
                { "version": 1, "panels": [ { "kind": "stat_tiles", "range": { "hours": {{hours}} }, "dataset": { "name": "{{dataset}}" } } ] }
                """).RootElement).Spec!;
            await using var scope = TenantOverviewService.OpenTenantScope(Request, person.Tenant, historyClamped: false, "test");
            var report = await new ReportSpecRunner(ReportSpecValidatorTests.Registry, Time).RunAsync(
                spec, new ReportRunContext(false, person.AllowedScopes, scope.ServiceProvider, new ReportReadLog()), CancellationToken.None);
            return report.Panels.Single().Rows.Single();
        }
    }

    private static FlagFactCondition TimeBelow(Harness harness, int days = 14, double value = 5) =>
        harness.Condition("glucose_statistics", "timeBelow", days, FlagComparison.Above, value, "%");

    private static FlagFactCondition TimeBelowOwnRange(Harness harness, double value = 5) =>
        harness.Condition("glucose_statistics", "timeBelowOwnRange", 14, FlagComparison.Above, value, "%");

    private static FlagFactCondition HoursSinceReading(Harness harness, double value = 1) =>
        harness.Condition("current_status", "hoursSinceReading", null, FlagComparison.Above, value, "h");

    // ---- figures and outcomes ----

    [Fact]
    public async Task The_evaluator_reads_the_same_figures_as_a_custom_report_for_one_person_and_range()
    {
        var harness = new Harness();
        var person = harness.Follow(new Person(Pattern, TimeSpan.FromDays(15), LatestAge: TimeSpan.FromMinutes(47)));
        var cv = harness.Condition("glucose_statistics", "cv", 14, FlagComparison.Above, 1, "%");

        var judged = await harness.JudgeAsync(person, TimeBelow(harness), cv, HoursSinceReading(harness, 0));
        var statistics = await harness.ReportAsync(person, "glucose_statistics", 14 * 24);
        var now = await harness.ReportAsync(person, "current_status", 24);

        judged[0].Value.Should().Be((double)statistics["timeBelow"]!).And.BeGreaterThan(0);
        judged[1].Value.Should().Be((double)statistics["cv"]!).And.BeGreaterThan(0);
        judged[2].Value.Should().Be((double)now["hoursSinceReading"]!).And.Be(0.8);
    }

    [Fact]
    public async Task A_figure_that_meets_the_comparison_is_matched_with_its_value_window_and_coverage()
    {
        var harness = new Harness();
        var person = harness.Follow(new Person(Pattern, TimeSpan.FromDays(15)));

        var judgement = (await harness.JudgeAsync(person, TimeBelow(harness))).Single();

        judgement.Outcome.Should().Be(FlagOutcome.Matched);
        judgement.Matches.Should().BeTrue();
        judgement.Value.Should().BeApproximately(10, 1);
        judgement.Window.Should().Be(new ReportWindow(Start.AddDays(-14).ToUnixTimeMilliseconds(), Start.ToUnixTimeMilliseconds()));
        judgement.Coverage.Should().BeGreaterThan(DataSufficiencyAssessment.MinimumCompleteness);
        judgement.Bound.Should().BeNull("time below the consensus range is not measured against the person's own range");
    }

    [Fact]
    public async Task A_figure_that_misses_the_comparison_is_not_matched()
    {
        var harness = new Harness();
        var person = harness.Follow(new Person(Pattern, TimeSpan.FromDays(15)));

        var judgement = (await harness.JudgeAsync(person, TimeBelow(harness, value: 50))).Single();

        judgement.Outcome.Should().Be(FlagOutcome.NotMatched);
        judgement.Matches.Should().BeFalse();
        judgement.Value.Should().BeApproximately(10, 1);
    }

    [Fact]
    public async Task No_data_in_the_range_is_never_a_match()
    {
        var harness = new Harness();
        var person = harness.Follow(new Person(Pattern, TimeSpan.Zero));

        var judged = await harness.JudgeAsync(person,
            TimeBelow(harness, value: -1),
            harness.Condition("glucose_statistics", "sensorWornPercent", 14, FlagComparison.Below, 50, "%"),
            harness.Condition("current_status", "hoursSinceReading", null, FlagComparison.AtLeast, 0, "h"));

        judged.Should().OnlyContain(j => j.Outcome == FlagOutcome.NoData && !j.Matches && j.Value == null);
    }

    [Fact]
    public async Task Too_little_data_is_judged_on_the_data_there_is_and_states_the_coverage()
    {
        var harness = new Harness();
        var person = harness.Follow(new Person(Pattern, TimeSpan.FromDays(2)));

        var judged = await harness.JudgeAsync(person, TimeBelow(harness), TimeBelow(harness, value: 50));

        judged[0].Outcome.Should().Be(FlagOutcome.TooLittleData);
        judged[0].Matches.Should().BeTrue();
        judged[0].Coverage.Should().BeApproximately(100.0 * 2 / 14, 0.5);
        judged[0].Value.Should().BeApproximately(10, 1);
        judged[1].Outcome.Should().Be(FlagOutcome.TooLittleData);
        judged[1].Matches.Should().BeFalse();
    }

    [Fact]
    public async Task Sensor_worn_below_a_value_is_matched_and_does_not_judge_its_own_coverage()
    {
        var harness = new Harness();
        var person = harness.Follow(new Person(Pattern, TimeSpan.FromDays(2)));

        var judgement = (await harness.JudgeAsync(person,
            harness.Condition("glucose_statistics", "sensorWornPercent", 14, FlagComparison.Below, 50, "%"))).Single();

        (judgement.Outcome, judgement.Matches, judgement.Coverage).Should().Be((FlagOutcome.Matched, true, (double?)null));
        judgement.Value.Should().BeApproximately(100.0 * 2 / 14, 0.5);
    }

    // ---- what a caller may check ----

    [Fact]
    public async Task A_caller_limited_to_24_hours_cannot_check_the_person_and_nothing_is_read()
    {
        var harness = new Harness();
        var person = harness.Follow(new Person(Pattern, TimeSpan.FromDays(15), LatestAge: TimeSpan.FromMinutes(5)), limitTo24Hours: true);

        var judged = await harness.JudgeAsync(person, TimeBelow(harness), HoursSinceReading(harness));

        judged.Should().OnlyContain(j => j.Outcome == FlagOutcome.CannotBeChecked && j.Uncheckable == FlagUncheckable.HistoryLimited);
        harness.RoundTrips.Should().Be(0);
    }

    [Fact]
    public async Task A_credential_limited_to_24_hours_cannot_check_anyone()
    {
        var harness = new Harness();
        var person = harness.Follow(new Person(Pattern, TimeSpan.FromDays(15)));

        var judged = await Harness.JudgeManyAsync(harness.Evaluator, [person], [TimeBelow(harness)], credentialLimitTo24Hours: true, CancellationToken.None);

        judged.Single().Single().Uncheckable.Should().Be(FlagUncheckable.HistoryLimited);
        harness.RoundTrips.Should().Be(0);
    }

    [Fact]
    public async Task A_caller_without_a_facts_scope_cannot_check_it_and_the_other_facts_still_judge()
    {
        var harness = new Harness();
        var person = harness.Follow(new Person(Pattern, TimeSpan.FromDays(15)));

        var judged = await harness.JudgeAsync(person,
            harness.Condition("device_status", "reservoir", null, FlagComparison.Below, 20, "U"),
            harness.Condition("glucose_statistics", "tdd", 14, FlagComparison.Above, 40, "U"),
            TimeBelow(harness));

        (judged[0].Outcome, judged[0].Uncheckable, judged[0].MissingScope)
            .Should().Be((FlagOutcome.CannotBeChecked, FlagUncheckable.MissingScope, Scope.DevicesRead));
        (judged[1].Outcome, judged[1].MissingScope).Should().Be((FlagOutcome.CannotBeChecked, Scope.TreatmentsRead));
        judged[2].Outcome.Should().Be(FlagOutcome.Matched);
    }

    [Fact]
    public async Task A_period_fact_needs_reports_read_and_a_live_fact_does_not()
    {
        var harness = new Harness();
        var data = new Person(Pattern, TimeSpan.FromDays(15), LatestAge: TimeSpan.FromHours(3));
        var without = harness.Follow(data, new HashSet<string> { Scope.GlucoseRead });
        var with = harness.Follow(data, Reports);

        var denied = await harness.JudgeAsync(without, TimeBelow(harness), HoursSinceReading(harness));
        var granted = await harness.JudgeAsync(with, TimeBelow(harness), HoursSinceReading(harness));

        (denied[0].Outcome, denied[0].MissingScope).Should().Be((FlagOutcome.CannotBeChecked, Scope.ReportsRead));
        (denied[1].Outcome, denied[1].Value).Should().Be((FlagOutcome.Matched, 3.0));
        granted.Select(j => j.Outcome).Should().Equal(FlagOutcome.Matched, FlagOutcome.Matched);
        harness.GlucoseReads.Should().Be(1, "nothing is read for the period fact the first caller may not check");
    }

    [Fact]
    public async Task An_own_range_fact_needs_therapy_read_and_without_it_no_schedule_is_read()
    {
        var harness = new Harness();
        var person = harness.Follow(new Person(Pattern, TimeSpan.FromDays(15), AllDay(80, 160)), Reports);

        var judged = await harness.JudgeAsync(person, TimeBelowOwnRange(harness), TimeBelow(harness));

        (judged[0].Outcome, judged[0].Uncheckable, judged[0].MissingScope)
            .Should().Be((FlagOutcome.CannotBeChecked, FlagUncheckable.MissingScope, Scope.TherapyRead));
        judged[1].Outcome.Should().Be(FlagOutcome.Matched);
        harness.ScheduleReads.Should().Be(0);
    }

    // ---- own range ----

    [Fact]
    public async Task Each_person_is_judged_against_their_own_range_and_the_bound_is_returned()
    {
        var harness = new Harness();
        var wide = harness.Follow(new Person(_ => 90, TimeSpan.FromDays(15), AllDay(80, 160)), Therapy);
        var tight = harness.Follow(new Person(_ => 90, TimeSpan.FromDays(15), AllDay(100, 200)), Therapy);
        var condition = TimeBelowOwnRange(harness, 50);

        var judged = await Harness.JudgeManyAsync(harness.Evaluator, [wide, tight], [condition], false, CancellationToken.None);

        var a = judged[0].Single();
        var b = judged[1].Single();
        (a.Outcome, a.Value, a.Bound).Should().Be((FlagOutcome.NotMatched, 0.0, new FlagBound(80, 160, false)));
        (b.Outcome, b.Value, b.Bound).Should().Be((FlagOutcome.Matched, 100.0, new FlagBound(100, 200, false)));
    }

    [Fact]
    public async Task A_schedule_that_varies_over_the_day_is_bound_by_its_lowest_low_and_highest_high_and_says_so()
    {
        var harness = new Harness();
        var person = harness.Follow(new Person(_ => 90, TimeSpan.FromDays(15), [("00:00", 100, 160), ("08:00", 80, 200)]), Therapy);

        var judgement = (await harness.JudgeAsync(person, TimeBelowOwnRange(harness))).Single();

        judgement.Bound.Should().Be(new FlagBound(80, 200, true));
        judgement.Value.Should().BeApproximately(100.0 / 3, 1, "readings of 90 are below the 100 low for the eight hours it holds");
    }

    [Fact]
    public async Task A_consensus_and_an_own_range_fact_with_one_comparison_and_value_are_each_judged_on_their_own_figure()
    {
        var harness = new Harness();
        var person = harness.Follow(new Person(_ => 90, TimeSpan.FromDays(15), AllDay(100, 200)), Therapy);
        var consensus = TimeBelow(harness, value: 4);
        var own = TimeBelowOwnRange(harness, 4);

        var first = await harness.JudgeAsync(person, consensus, own);
        var kept = await harness.JudgeAsync(person, own, consensus);

        (first[0].Outcome, first[0].Value).Should().Be((FlagOutcome.NotMatched, 0.0));
        (first[1].Outcome, first[1].Value).Should().Be((FlagOutcome.Matched, 100.0));
        (kept[0].Outcome, kept[1].Outcome).Should().Be((FlagOutcome.Matched, FlagOutcome.NotMatched));
        harness.GlucoseReads.Should().Be(1, "both facts come from one read of one range");
    }

    [Fact]
    public async Task A_person_without_a_target_range_has_no_data_for_an_own_range_fact()
    {
        var harness = new Harness();
        var person = harness.Follow(new Person(_ => 90, TimeSpan.FromDays(15)), Therapy);

        var judgement = (await harness.JudgeAsync(person,
            harness.Condition("glucose_statistics", "timeBelowOwnRange", 14, FlagComparison.AtLeast, 0, "%"))).Single();

        judgement.Outcome.Should().Be(FlagOutcome.NoData);
    }

    [Fact]
    public async Task A_failed_schedule_read_leaves_the_own_range_fact_without_data_and_the_consensus_fact_judged()
    {
        var harness = new Harness { ScheduleFails = true };
        var person = harness.Follow(new Person(Pattern, TimeSpan.FromDays(15), AllDay(80, 160)), Therapy);

        var judged = await harness.JudgeAsync(person, TimeBelowOwnRange(harness), TimeBelow(harness));

        (judged[0].Outcome, judged[0].Bound).Should().Be((FlagOutcome.NoData, (FlagBound?)null));
        judged[1].Outcome.Should().Be(FlagOutcome.Matched);
        judged[1].Value.Should().BeApproximately(10, 1);
        harness.ScheduleReads.Should().Be(1);
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(NullReferenceException))]
    [InlineData(typeof(TimeoutException))]
    public async Task A_defect_reading_the_schedule_fails_the_persons_read_rather_than_emptying_the_own_range_fact(Type defect)
    {
        var harness = new Harness { ScheduleDefect = (Exception)Activator.CreateInstance(defect)! };
        var person = harness.Follow(new Person(Pattern, TimeSpan.FromDays(15), AllDay(80, 160)), Therapy);
        IReadOnlyList<FlagFactCondition> conditions = [TimeBelowOwnRange(harness)];

        await Harness.JudgeOnceAsync(harness.Evaluator, [person], conditions, false, CancellationToken.None);
        await harness.Reads.WhenIdleAsync();
        var (judged, _) = await Harness.JudgeOnceAsync(harness.Evaluator, [person], conditions, false, CancellationToken.None);

        judged.Single().State.Should().Be(FlagPersonState.Failed);
        harness.Log.Errors.Should().ContainSingle();
    }

    [Fact]
    public async Task Consensus_facts_read_no_target_range_schedule()
    {
        var harness = new Harness();
        var person = harness.Follow(new Person(Pattern, TimeSpan.FromDays(15), AllDay(80, 160)), Therapy);

        await harness.JudgeAsync(person, TimeBelow(harness), harness.Condition("glucose_statistics", "cv", 14, FlagComparison.Above, 36, "%"));

        harness.ScheduleReads.Should().Be(0);
    }

    // ---- the keep ----

    [Fact]
    public async Task A_period_fact_is_read_once_inside_its_keep_and_read_again_behind_the_call_that_finds_it_ageing()
    {
        var harness = new Harness();
        var person = harness.Follow(new Person(Pattern, TimeSpan.FromDays(15)));
        var condition = TimeBelow(harness);
        var life = FlagFactEvaluator.KeepLife(person.Tenant.Id, condition.ReadKey, ReportFlagFactKind.Period);
        life.Should().BeGreaterThan(FlagFactEvaluator.PeriodKeep * (1 - FlagFactEvaluator.KeepSpread))
            .And.BeLessThanOrEqualTo(FlagFactEvaluator.PeriodKeep);
        var ageing = life * FlagFactEvaluator.RefreshAfter;

        var first = (await harness.JudgeAsync(person, condition)).Single();
        harness.Time.Advance(ageing - TimeSpan.FromTicks(1));
        var second = (await harness.JudgeAsync(person, condition)).Single();
        harness.GlucoseReads.Should().Be(1);
        second.Should().Be(first, "a kept figure states the window it was read over");

        harness.Time.Advance(TimeSpan.FromTicks(1));
        var served = (await harness.JudgeAsync(person, condition)).Single();
        served.Should().Be(first, "an ageing keep is still served, and read again behind the call");
        harness.GlucoseReads.Should().Be(2);

        var third = (await harness.JudgeAsync(person, condition)).Single();
        third.Window!.To.Should().Be(Start.Add(ageing).ToUnixTimeMilliseconds());
        harness.GlucoseReads.Should().Be(2);
    }

    [Fact]
    public async Task Each_keep_runs_out_its_own_life_after_its_own_read()
    {
        var harness = new Harness();
        var early = harness.Follow(new Person(Pattern, TimeSpan.FromDays(15)));
        var late = harness.Follow(new Person(Pattern, TimeSpan.FromDays(15)));
        var condition = TimeBelow(harness);
        var gap = TimeSpan.FromMinutes(1);
        var earlyEnds = FlagFactEvaluator.KeepLife(early.Tenant.Id, condition.ReadKey, ReportFlagFactKind.Period);
        var lateEnds = gap + FlagFactEvaluator.KeepLife(late.Tenant.Id, condition.ReadKey, ReportFlagFactKind.Period);
        var (first, second) = earlyEnds <= lateEnds ? (early, late) : (late, early);

        await harness.JudgeAsync(early, condition);
        harness.Time.Advance(gap);
        await harness.JudgeAsync(late, condition);
        harness.Time.Advance(TimeSpan.FromTicks(Math.Min(earlyEnds.Ticks, lateEnds.Ticks)) - gap);
        var (judged, pending) = await Harness.JudgeOnceAsync(harness.Evaluator, [first, second], [condition], false, CancellationToken.None);

        judged.Select(j => j.State).Should().Equal(
            [FlagPersonState.Pending, FlagPersonState.Judged], "only the keep whose own life has run out since its read is pending");
        pending.Should().Be(1);
    }

    [Fact]
    public async Task People_read_in_one_call_are_read_again_spread_over_several_minutes_and_a_page_polling_each_minute_never_waits()
    {
        const int people = 24;
        var harness = new Harness();
        var followed = Enumerable.Range(0, people).Select(_ => harness.Follow(new Person(Pattern, TimeSpan.FromDays(15)))).ToList();
        var condition = TimeBelow(harness);
        await Harness.JudgeManyAsync(harness.Evaluator, followed, [condition], false, CancellationToken.None);
        harness.GlucoseReads.Should().Be(people);

        var readsEachMinute = new List<int>();
        var lastRefresh = (int)Math.Ceiling(FlagFactEvaluator.PeriodKeep.TotalMinutes * FlagFactEvaluator.RefreshAfter);
        for (var minute = 1; minute <= lastRefresh; minute++)
        {
            harness.Time.Advance(TimeSpan.FromMinutes(1));
            var before = harness.GlucoseReads;
            var (_, pending) = await Harness.JudgeOnceAsync(harness.Evaluator, followed, [condition], false, CancellationToken.None);
            await harness.Reads.WhenIdleAsync();
            pending.Should().Be(0, "a keep read again before its life runs out is never missing for a page that polls");
            readsEachMinute.Add(harness.GlucoseReads - before);
        }

        output.WriteLine($"Reads each minute: {string.Join(", ", readsEachMinute)}.");
        readsEachMinute.Should().OnlyContain(reads => reads < people, "the keeps of one call age spread over several minutes");
        readsEachMinute.Sum().Should().Be(people, "every keep is read again once by two thirds of the longest life");
    }

    [Fact]
    public async Task A_changed_range_is_a_new_read_and_a_changed_value_or_comparison_is_judged_on_the_kept_figures()
    {
        var harness = new Harness();
        var person = harness.Follow(new Person(Pattern, TimeSpan.FromDays(15)));

        await harness.JudgeAsync(person, TimeBelow(harness, days: 14));
        await harness.JudgeAsync(person, TimeBelow(harness, days: 7));
        harness.GlucoseReads.Should().Be(2, "a different range is a different read");

        var narrowed = (await harness.JudgeAsync(person, TimeBelow(harness, days: 7, value: 50))).Single();
        var turned = (await harness.JudgeAsync(person,
            harness.Condition("glucose_statistics", "timeBelow", 7, FlagComparison.AtMost, 50, "%"))).Single();

        harness.GlucoseReads.Should().Be(2, "a threshold edit costs no read");
        narrowed.Outcome.Should().Be(FlagOutcome.NotMatched);
        turned.Outcome.Should().Be(FlagOutcome.Matched);
    }

    [Fact]
    public async Task A_live_fact_is_read_on_every_call()
    {
        var harness = new Harness();
        var person = harness.Follow(new Person(Pattern, TimeSpan.FromDays(1), LatestAge: TimeSpan.FromMinutes(30)));
        var since = HoursSinceReading(harness);

        var first = (await harness.JudgeAsync(person, since)).Single();
        harness.Time.Advance(TimeSpan.FromMinutes(40));
        var second = (await harness.JudgeAsync(person, since)).Single();

        harness.LatestReads.Should().Be(2);
        (first.Outcome, first.Value, first.Window).Should().Be((FlagOutcome.NotMatched, 0.5, (ReportWindow?)null));
        (second.Outcome, second.Value).Should().Be((FlagOutcome.Matched, 1.2));
    }

    [Fact]
    public async Task Facts_and_values_from_one_dataset_and_range_share_one_read()
    {
        var harness = new Harness();
        var person = harness.Follow(new Person(Pattern, TimeSpan.FromDays(15)));

        var judged = await harness.JudgeAsync(person,
            TimeBelow(harness), TimeBelow(harness, value: 50), harness.Condition("glucose_statistics", "cv", 14, FlagComparison.Above, 36, "%"));

        harness.GlucoseReads.Should().Be(1);
        judged.Select(j => j.Outcome).Should().Equal(FlagOutcome.Matched, FlagOutcome.NotMatched, FlagOutcome.NotMatched);
    }

    [Fact]
    public async Task A_caller_without_therapy_read_is_never_served_figures_kept_for_a_caller_with_it()
    {
        var harness = new Harness();
        var data = new Person(Pattern, TimeSpan.FromDays(15), AllDay(80, 160));
        var withTherapy = harness.Follow(data, Therapy);
        var withoutTherapy = withTherapy with { AllowedScopes = Reports, EffectivePermissions = Reports };
        var other = Guid.NewGuid();

        await Harness.JudgeAsync(harness.Evaluator, withTherapy, TimeBelow(harness), TimeBelowOwnRange(harness));
        var denied = await Harness.JudgeAsync(harness.For(other), withoutTherapy, TimeBelow(harness), TimeBelowOwnRange(harness));
        harness.GlucoseReads.Should().Be(2, "the narrower caller's figures are read under its own scopes, not taken from the wider keep");
        harness.ScheduleReads.Should().Be(1);
        denied[1].MissingScope.Should().Be(Scope.TherapyRead);

        await Harness.JudgeAsync(harness.For(Guid.NewGuid()), withTherapy, TimeBelowOwnRange(harness), TimeBelow(harness));
        harness.GlucoseReads.Should().Be(2, "a caller with the same scopes is served the wider keep");

        var rows = await harness.AuditRowsAsync();
        rows.Single(r => r.ActorSubjectId == other).EntityType.Should().Be("glucose", "the narrower caller was served no profile values");
    }

    [Fact]
    public async Task A_caller_with_more_scopes_is_not_served_figures_kept_for_a_caller_with_fewer()
    {
        var harness = new Harness();
        var narrower = harness.Follow(new Person(Pattern, TimeSpan.FromDays(15)));
        var scopes = new HashSet<string>(Reports) { Scope.TreatmentsRead };
        var wider = narrower with { AllowedScopes = scopes, EffectivePermissions = scopes };
        var tdd = harness.Condition("glucose_statistics", "tdd", 14, FlagComparison.Above, 1, "U");

        await harness.JudgeAsync(narrower, TimeBelow(harness));
        var judged = await Harness.JudgeAsync(harness.For(Guid.NewGuid()), wider, TimeBelow(harness), tdd);
        harness.GlucoseReads.Should().Be(2, "the row kept for the narrower caller holds no insulin figures");
        var report = await harness.ReportAsync(wider, "glucose_statistics", 14 * 24);

        judged[0].Outcome.Should().Be(FlagOutcome.Matched);
        (judged[1].Outcome, judged[1].Value).Should().Be((FlagOutcome.Matched, (double?)report["tdd"]));
        judged[1].Value.Should().BePositive();
    }

    // ---- the read audit ----

    [Fact]
    public async Task Each_viewer_gets_one_audit_row_per_person_for_each_keep_read_or_kept()
    {
        var harness = new Harness();
        var person = harness.Follow(new Person(Pattern, TimeSpan.FromDays(15)));
        var second = Guid.NewGuid();

        await harness.JudgeAsync(person, TimeBelow(harness));
        await harness.JudgeAsync(person, TimeBelow(harness, value: 50));
        await Harness.JudgeAsync(harness.For(second), person, TimeBelow(harness));
        await Harness.JudgeAsync(harness.For(second), person, TimeBelow(harness));
        harness.Time.Advance(FlagFactEvaluator.PeriodKeep);
        await harness.JudgeAsync(person, TimeBelow(harness));

        harness.GlucoseReads.Should().Be(2);
        var rows = await harness.AuditRowsAsync();
        rows.Select(r => r.ActorSubjectId).Should().Equal(
            [Viewer, second, Viewer], "a second viewer of kept figures gets its own row, the same viewer none until they are read again");
    }

    [Fact]
    public async Task The_audit_row_names_the_caller_the_endpoint_the_categories_and_the_facts()
    {
        var harness = new Harness();
        var person = harness.Follow(new Person(Pattern, TimeSpan.FromDays(15), AllDay(80, 160), TimeSpan.FromMinutes(20)), Therapy);

        await harness.JudgeAsync(person, TimeBelowOwnRange(harness), TimeBelow(harness), HoursSinceReading(harness));

        var row = (await harness.AuditRowsAsync()).Should().ContainSingle().Subject;
        (row.TenantId, row.ActorSubjectId, row.ActorName, row.AuthType, row.Endpoint, row.StatusCode)
            .Should().Be((person.Tenant.Id, Viewer, "Viewer", "Session", FlagFactEvaluator.AuditEndpoint, 200));
        row.EntityType.Should().Be("glucose,profiles");
        row.RecordCount.Should().Be(14 * 288 + 1, "the 14 days' readings and the latest one");
        JsonSerializer.Deserialize<Dictionary<string, string[]>>(row.QueryParametersJson!)!["facts"].Should().Equal(
            "glucose_statistics.timeBelowOwnRange over 14 days", "glucose_statistics.timeBelow over 14 days", "current_status.hoursSinceReading now");
    }

    [Fact]
    public async Task A_live_read_is_audited_once_a_keep_for_each_viewer()
    {
        var harness = new Harness();
        var person = harness.Follow(new Person(Pattern, TimeSpan.FromDays(1), LatestAge: TimeSpan.FromMinutes(30)));

        await harness.JudgeAsync(person, HoursSinceReading(harness));
        await harness.JudgeAsync(person, HoursSinceReading(harness));
        harness.Time.Advance(FlagFactEvaluator.PeriodKeep);
        await harness.JudgeAsync(person, HoursSinceReading(harness));

        harness.LatestReads.Should().Be(3, "a live fact is judged on every call");
        (await harness.AuditRowsAsync()).Should().HaveCount(2).And.OnlyContain(r => r.EntityType == "glucose");
    }

    [Fact]
    public async Task A_person_whose_background_read_fails_is_left_unjudged_for_the_keep_logged_once_and_the_others_are_judged()
    {
        var harness = new Harness();
        var served = harness.Follow(new Person(Pattern, TimeSpan.FromDays(15)));
        var failing = harness.Follow(new Person(Pattern, TimeSpan.FromDays(15), ReadFailsAfter: Task.CompletedTask));
        IReadOnlyList<FlagFactCondition> conditions = [TimeBelow(harness)];

        await Harness.JudgeOnceAsync(harness.Evaluator, [served, failing], conditions, false, CancellationToken.None);
        await harness.Reads.WhenIdleAsync();
        var (judged, pending) = await Harness.JudgeOnceAsync(harness.Evaluator, [served, failing], conditions, false, CancellationToken.None);
        var (again, _) = await Harness.JudgeOnceAsync(harness.Evaluator, [served, failing], conditions, false, CancellationToken.None);
        await harness.Reads.WhenIdleAsync();

        judged.Select(j => j.State).Should().Equal(FlagPersonState.Judged, FlagPersonState.Failed);
        pending.Should().Be(0, "a failed read is not still being checked");
        judged[0].Judgements.Single().Outcome.Should().Be(FlagOutcome.Matched);
        again[1].State.Should().Be(FlagPersonState.Failed, "the failure is kept, so the person is not read again on every call");
        harness.GlucoseReads.Should().Be(2);
        harness.Log.Errors.Should().ContainSingle("the failure is logged once for the keep it makes");
        (await harness.AuditRowsAsync()).Should().ContainSingle().Which.TenantId.Should().Be(served.Tenant.Id);
    }

    [Fact]
    public async Task A_call_cancelled_while_it_writes_its_rows_leaves_a_row_owed_for_each_person_it_did_not_write()
    {
        var harness = new Harness();
        var followed = Enumerable.Range(0, 3).Select(_ => harness.Follow(new Person(Pattern, TimeSpan.FromDays(15)))).ToList();
        using var cancel = new CancellationTokenSource();
        var settingReads = 0;
        harness.OnAuditSettingRead = () =>
        {
            if (++settingReads == 2)
                cancel.Cancel();
        };

        var cancelled = () => Harness.JudgeManyAsync(harness.Evaluator, followed, [TimeBelow(harness)], false, cancel.Token);
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        harness.OnAuditSettingRead = null;
        await Harness.JudgeManyAsync(harness.Evaluator, followed, [TimeBelow(harness)], false, CancellationToken.None);
        await Harness.JudgeManyAsync(harness.Evaluator, followed, [TimeBelow(harness)], false, CancellationToken.None);

        harness.GlucoseReads.Should().Be(3);
        (await harness.AuditRowsAsync()).Select(r => r.TenantId).Should().Equal(
            [.. followed.Select(p => p.Tenant.Id)], "the first two rows were written before the cancel, the third on the next call");
    }

    [Theory]
    [InlineData("off")]
    [InlineData("unreadable")]
    public async Task Figures_served_when_no_row_could_be_written_get_their_row_when_next_served(string setting)
    {
        var harness = new Harness { ReadAuditEnabled = setting != "off", AuditSettingFails = setting == "unreadable" };
        var person = harness.Follow(new Person(Pattern, TimeSpan.FromDays(15)));

        await harness.JudgeAsync(person, TimeBelow(harness));
        (harness.ReadAuditEnabled, harness.AuditSettingFails) = (true, false);
        await harness.JudgeAsync(person, TimeBelow(harness));
        await harness.JudgeAsync(person, TimeBelow(harness));

        harness.GlucoseReads.Should().Be(1);
        (await harness.AuditRowsAsync()).Should().ContainSingle("the kept figures get one row, on the first serving that could write it");
    }

    // ---- the batch ----

    [Fact]
    public async Task Three_hundred_people_with_two_conditions_are_pending_without_a_read_cold_and_judged_warm_with_no_read()
    {
        const int people = 300;
        var harness = new Harness();
        var followed = Enumerable.Range(0, people)
            .Select(_ => harness.Follow(new Person(Pattern, TimeSpan.FromDays(15), AllDay(70, 180), TimeSpan.FromMinutes(10))))
            .ToList();
        IReadOnlyList<FlagFactCondition> conditions = [TimeBelow(harness), HoursSinceReading(harness)];
        var warmUp = Enumerable.Range(0, 8)
            .Select(_ => harness.Follow(new Person(Pattern, TimeSpan.FromDays(15), AllDay(70, 180), TimeSpan.FromMinutes(10))))
            .ToList();
        var first = Stopwatch.StartNew();
        await Harness.JudgeManyAsync(harness.Evaluator, warmUp, conditions, false, CancellationToken.None);
        output.WriteLine($"First call of the process, {warmUp.Count} people: {first.Elapsed.TotalMilliseconds:0} ms.");
        var warmUpTrips = harness.RoundTrips;
        var warmUpLatest = harness.LatestReads;

        var clock = Stopwatch.StartNew();
        var (cold, pending) = await Harness.JudgeOnceAsync(harness.Evaluator, followed, conditions, false, CancellationToken.None);
        var coldPass = clock.Elapsed;
        var coldLatest = harness.LatestReads - warmUpLatest;
        clock.Restart();
        await harness.Reads.WhenIdleAsync();
        var background = clock.Elapsed;
        // The cold pass starts the background reads, which may make round trips before it returns, so the two are
        // counted together; the cold pass's own reads are the latest readings.
        var coldAndBackgroundTrips = harness.RoundTrips - warmUpTrips;
        var backgroundLatest = harness.LatestReads - warmUpLatest - coldLatest;
        clock.Restart();
        var (warm, warmPending) = await Harness.JudgeOnceAsync(harness.Evaluator, followed, conditions, false, CancellationToken.None);
        var warmPass = clock.Elapsed;
        var warmTrips = harness.RoundTrips - warmUpTrips - coldAndBackgroundTrips;

        output.WriteLine($"{people} people, 2 conditions, limit {harness.Reads.Size}: cold pass {coldPass.TotalMilliseconds:0} ms, "
            + $"{coldLatest} latest reads; background {background.TotalMilliseconds:0} ms, {coldAndBackgroundTrips} round trips with the cold pass; "
            + $"warm pass {warmPass.TotalMilliseconds:0} ms, {warmTrips} round trips.");
        (pending, warmPending).Should().Be((people, 0));
        cold.Should().OnlyContain(p => p.State == FlagPersonState.Pending);
        warm.Should().OnlyContain(p => p.State == FlagPersonState.Judged
            && p.Judgements[0].Outcome == FlagOutcome.Matched
            && p.Judgements[1].Outcome == FlagOutcome.NotMatched);
        coldLatest.Should().Be(people, "the cold pass reads only the latest reading the overview reads anyway");
        backgroundLatest.Should().Be(0);
        coldAndBackgroundTrips.Should().Be(people * 3, "the cold pass's latest reading, and per person in the background: the readings and the time zone");
        warmTrips.Should().Be(people, "a kept period fact reads nothing; the live fact is judged on the overview's latest reading");
    }
}
