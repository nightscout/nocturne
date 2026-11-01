using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Seeding;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.DeviceStatuses;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Sleep;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Core.Models.V4;
using Nocturne.Services.Demo.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Seeding;

/// <summary>
/// A seed for a tenant whose patient record names Pacific/Auckland, on a host whose zone is UTC. The
/// instant is Saturday 20:00 UTC, already Sunday 09:00 in Auckland, so a row laid out in the host's
/// day lands thirteen hours away from the one the tenant reads.
/// </summary>
public class SampleDataSeederMixedZoneTests : IDisposable
{
    private const string Auckland = "Pacific/Auckland";
    private static readonly Guid TenantId = Guid.Parse("0196a3f2-7b1e-7c3a-9d52-4e8f1a6b2c10");
    private static readonly DateTimeOffset Instant = new(2026, 3, 14, 20, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo AucklandZone = TimeZoneInfo.FindSystemTimeZoneById(Auckland);

    private readonly SqliteTestDatabase _db = TestDbContextFactory.CreateSqliteWithTenant(TenantId);
    private readonly List<Entry> _entries = [];
    private readonly List<SleepSession> _sleep = [];
    private readonly List<Treatment> _deviceChanges = [];
    private readonly FakeTimeProvider _time = new(Instant);

    public SampleDataSeederMixedZoneTests() => _time.SetLocalTimeZone(TimeZoneInfo.Utc);

    public void Dispose() => _db.Dispose();

    private SampleDataSeeder CreateSeeder()
    {
        var tenant = new Mock<ITenantAccessor>();
        tenant.SetupGet(t => t.TenantId).Returns(TenantId);

        var entries = new Mock<IEntryService>();
        entries
            .Setup(e => e.CreateEntriesAsync(It.IsAny<IEnumerable<Entry>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<Entry> batch, WriteOrigin _, CancellationToken _) =>
            {
                var written = batch.ToList();
                _entries.AddRange(written);
                return new BulkWrite<Entry>(written, 0);
            });

        var statuses = new Mock<IDeviceStatusService>();
        statuses
            .Setup(d => d.ImportDeviceStatusesAsync(It.IsAny<IReadOnlyList<DeviceStatus>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecompositionResult());

        var sleep = new Mock<ISleepService>();
        sleep
            .Setup(s => s.UpsertSessionAsync(It.IsAny<SleepSession>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SleepSession session, CancellationToken _) =>
            {
                _sleep.Add(session);
                return session;
            });

        var treatments = new Mock<ITreatmentService>();
        treatments
            .Setup(t => t.ImportTreatmentsAsync(It.IsAny<IReadOnlyList<Treatment>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecompositionResult());
        treatments
            .Setup(t => t.CreateTreatmentsAsync(It.IsAny<IEnumerable<Treatment>>(), WriteOrigin.Backfill, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<Treatment> batch, WriteOrigin _, CancellationToken _) =>
            {
                var written = batch.ToList();
                _deviceChanges.AddRange(written);
                return new BulkWrite<Treatment>(written, 0);
            });

        return new SampleDataSeeder(
            tenant.Object,
            _db.ContextFactory.AsNocturneFactory(),
            new SubjectRepository(_db.ContextFactory.AsNocturneFactory()),
            entries.Object,
            treatments.Object,
            sleep.Object,
            Mock.Of<IHeartRateService>(),
            Mock.Of<IStepCountService>(),
            Mock.Of<ITrackerRepository>(),
            new AlertRuleRepository(_db.ContextFactory.AsNocturneFactory()),
            Mock.Of<IRuleScopeClassifier>(),
            Mock.Of<IProfileWriteService>(),
            statuses.Object,
            Mock.Of<ICompressionLowDetectionService>(),
            Mock.Of<ICompressionLowService>(),
            Mock.Of<ICompressionLowRepository>(),
            new TreatmentFoodRepository(_db.ContextFactory.AsNocturneFactory()),
            new StateSpanRepository(
                _db.ContextFactory.AsNocturneFactory(), Mock.Of<IDeduplicationService>(), new SystemAuditContext(),
                NullLogger<StateSpanRepository>.Instance),
            new ProfileActivationRepository(
                _db.ContextFactory.AsNocturneFactory(), new SystemAuditContext(), NullLogger<ProfileActivationRepository>.Instance),
            new TempBasalRepository(
                _db.ContextFactory.AsNocturneFactory(), Mock.Of<IDeduplicationService>(), new SystemAuditContext(),
                NullLogger<TempBasalRepository>.Instance),
            _time,
            NullLoggerFactory.Instance,
            NullLogger<SampleDataSeeder>.Instance);
    }

    private async Task RecordZoneAsync(string zone)
    {
        await using var db = _db.CreateContext();
        db.PatientRecords.Add(new PatientRecordEntity { Id = Guid.CreateVersion7(), TenantId = TenantId, Timezone = zone });
        await db.SaveChangesAsync();
    }

    private Task SeedAsync(int days = 2) => CreateSeeder().SeedAsync(
        new TenantContext(TenantId, "test", "Test", true, false),
        days,
        ownerSubjectId: null);

    private static DateTime InAuckland(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, AucklandZone);

    private static DateTime InAuckland(long mills) => InAuckland(DateTimeOffset.FromUnixTimeMilliseconds(mills).UtcDateTime);

    [Fact]
    public async Task The_glucose_stream_runs_from_the_tenants_midnight_to_the_providers_instant()
    {
        await RecordZoneAsync(Auckland);

        await SeedAsync(days: 3);

        var primary = _entries
            .Where(e => e.Type == "sgv" && e.Device == DemoCoverageSeeds.PrimaryCgmSerial)
            .Select(e => e.Mills)
            .ToList();
        InAuckland(primary.Min()).Should().Be(new DateTime(2026, 3, 12), "the window opens at Auckland midnight three days back");
        InAuckland(primary.Max()).Should().Be(new DateTime(2026, 3, 15, 8, 55, 0), "the last five-minute step before 09:00 in Auckland");
    }

    [Fact]
    public async Task Sleep_falls_in_the_tenants_night()
    {
        await RecordZoneAsync(Auckland);

        await SeedAsync(days: 3);

        _sleep.Should().NotBeEmpty();
        foreach (var session in _sleep)
        {
            var bedtime = InAuckland(session.StartTime);
            bedtime.TimeOfDay.Should().BeGreaterThanOrEqualTo(TimeSpan.FromHours(22), $"bedtime {bedtime:g} is at night in Auckland");
            bedtime.TimeOfDay.Should().BeLessThanOrEqualTo(TimeSpan.FromHours(23.5));
            InAuckland(session.EndTime).Date.Should().Be(bedtime.Date.AddDays(1), "the night ends the next Auckland morning");
        }
    }

    [Fact]
    public async Task Device_changes_land_at_their_change_time_on_the_tenants_day()
    {
        await RecordZoneAsync(Auckland);

        await SeedAsync(days: 7);

        _deviceChanges.Should().NotBeEmpty();
        foreach (var change in _deviceChanges)
        {
            var local = InAuckland(change.Mills);
            DemoDeviceLifecycle.ChangeTimeOn(local.Date, change.EventType!)
                .Should().Be(local, $"{change.EventType} at {local:g} is a change time on its Auckland day");
        }
    }

    [Fact]
    public async Task The_travel_span_matches_the_trip_on_the_timezone_timeline()
    {
        await RecordZoneAsync(Auckland);

        await SeedAsync(days: DemoLifestyleSeeds.TripStartDaysAgo + 1);

        await using var db = _db.CreateContext();
        var timeline = await db.TimezoneTimeline.OrderBy(t => t.EffectiveFrom).ToListAsync();
        var trip = timeline.Single(t => t.Timezone == DemoLifestyleSeeds.TripTimezone);
        var home = timeline.First(t => t.EffectiveFrom > trip.EffectiveFrom);
        var travel = await db.StateSpans.SingleAsync(s => s.Category == nameof(StateSpanCategory.Travel));

        InAuckland(travel.StartTimestamp).Should().Be(trip.EffectiveFrom);
        InAuckland(travel.EndTimestamp!.Value).Should().Be(home.EffectiveFrom);
    }

    [Fact]
    public async Task The_seeders_own_rows_follow_the_zone_on_the_patient_record()
    {
        await RecordZoneAsync(Auckland);

        await SeedAsync();

        await using var db = _db.CreateContext();
        (await db.BodyWeights.AnyAsync(w => w.SyncIdentifier == "demo-weight-2026-03-15"))
            .Should().BeTrue("it is already Sunday in Auckland, though still Saturday on the host");
        (await db.TimezoneTimeline.Select(t => t.Timezone).Distinct().ToListAsync()).Should().BeEquivalentTo([Auckland]);
        (await db.PatientRecords.SingleAsync()).Timezone.Should().Be(Auckland);
    }

    [Fact]
    public async Task Without_a_recorded_zone_the_seed_lays_its_days_out_in_the_hosts()
    {
        await SeedAsync(days: 10);

        await using var db = _db.CreateContext();
        (await db.BodyWeights.AnyAsync(w => w.SyncIdentifier == "demo-weight-2026-03-15"))
            .Should().BeFalse("it is still Saturday on the host");
        (await db.BodyWeights.AnyAsync(w => w.SyncIdentifier == "demo-weight-2026-03-08")).Should().BeTrue();
        var primary = _entries.Where(e => e.Type == "sgv" && e.Device == DemoCoverageSeeds.PrimaryCgmSerial).ToList();
        DateTimeOffset.FromUnixTimeMilliseconds(primary.Max(e => e.Mills)).UtcDateTime
            .Should().Be(new DateTime(2026, 3, 14, 19, 55, 0, DateTimeKind.Utc));
    }
}
