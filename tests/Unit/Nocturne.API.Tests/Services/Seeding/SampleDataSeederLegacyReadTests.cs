using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Seeding;
using Nocturne.API.Tests.TestDoubles;
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
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Seeding;

/// <summary>
/// The seeder's writes after its decomposer flushes reach the legacy read caches. The treatment,
/// entry and device status services are doubles that report nothing, so every report here is the
/// seeder's own.
/// </summary>
public class SampleDataSeederLegacyReadTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("0196a3f2-7b1e-7c3a-9d52-4e8f1a6b2c10");
    private static readonly DateTimeOffset Instant = new(2026, 3, 14, 20, 0, 0, TimeSpan.Zero);

    private readonly SqliteTestDatabase _db = TestDbContextFactory.CreateSqliteWithTenant(TenantId);
    private readonly RecordingLegacyReads _written = new();
    private readonly FakeTimeProvider _time = new(Instant);

    public SampleDataSeederLegacyReadTests() => _time.SetLocalTimeZone(TimeZoneInfo.Utc);

    public void Dispose() => _db.Dispose();

    private SampleDataSeeder CreateSeeder()
    {
        var contexts = _db.ContextFactory.AsNocturneFactory();
        var tenant = new Mock<ITenantAccessor>();
        tenant.SetupGet(t => t.TenantId).Returns(TenantId);

        var entries = new Mock<IEntryService>();
        entries
            .Setup(e => e.CreateEntriesAsync(It.IsAny<IEnumerable<Entry>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<Entry> batch, WriteOrigin _, CancellationToken _) => new BulkWrite<Entry>(batch.ToList(), 0));

        var statuses = new Mock<IDeviceStatusService>();
        statuses
            .Setup(d => d.ImportDeviceStatusesAsync(It.IsAny<IReadOnlyList<DeviceStatus>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecompositionResult());

        var sleep = new Mock<ISleepService>();
        sleep
            .Setup(s => s.UpsertSessionAsync(It.IsAny<SleepSession>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SleepSession session, CancellationToken _) => session);

        var treatments = new Mock<ITreatmentService>();
        treatments
            .Setup(t => t.ImportTreatmentsAsync(It.IsAny<IReadOnlyList<Treatment>>(), It.IsAny<CancellationToken>()))
            .Returns(async (IReadOnlyList<Treatment> batch, CancellationToken _) =>
            {
                await DecomposeAsync(batch);
                return new DecompositionResult();
            });
        treatments
            .Setup(t => t.CreateTreatmentsAsync(It.IsAny<IEnumerable<Treatment>>(), WriteOrigin.Backfill, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<Treatment> batch, WriteOrigin _, CancellationToken _) => new BulkWrite<Treatment>(batch.ToList(), 0));

        var audit = new SystemAuditContext();
        return new SampleDataSeeder(
            tenant.Object,
            contexts,
            new SubjectRepository(contexts),
            entries.Object,
            treatments.Object,
            sleep.Object,
            Mock.Of<IHeartRateService>(),
            Mock.Of<IStepCountService>(),
            Mock.Of<ITrackerRepository>(),
            new AlertRuleRepository(contexts),
            Mock.Of<IRuleScopeClassifier>(),
            Mock.Of<IProfileWriteService>(),
            statuses.Object,
            Mock.Of<ICompressionLowDetectionService>(),
            Mock.Of<ICompressionLowService>(),
            Mock.Of<ICompressionLowRepository>(),
            new TreatmentFoodRepository(contexts, _written),
            new StateSpanRepository(
                contexts, Mock.Of<IDeduplicationService>(), audit, NullLogger<StateSpanRepository>.Instance, _written),
            new ProfileActivationRepository(
                contexts, audit, NullLogger<ProfileActivationRepository>.Instance, legacyReads: _written),
            new TempBasalRepository(
                contexts, Mock.Of<IDeduplicationService>(), audit, NullLogger<TempBasalRepository>.Instance, legacyReads: _written),
            _time,
            NullLoggerFactory.Instance,
            NullLogger<SampleDataSeeder>.Instance);
    }

    private async Task DecomposeAsync(IReadOnlyList<Treatment> batch)
    {
        await using var db = _db.CreateContext();
        foreach (var treatment in batch)
        {
            var at = DateTimeOffset.FromUnixTimeMilliseconds(treatment.Mills).UtcDateTime;
            if (treatment.EventType == "Carbs")
                db.CarbIntakes.Add(new CarbIntakeEntity
                {
                    Id = Guid.CreateVersion7(), TenantId = TenantId, Timestamp = at, Carbs = treatment.Carbs ?? 0,
                });
            else if (treatment.EventType == "Temp Basal")
                db.TempBasals.Add(new TempBasalEntity
                {
                    Id = Guid.CreateVersion7(), TenantId = TenantId, Timestamp = at, DataSource = treatment.DataSource,
                    Origin = nameof(TempBasalOrigin.Algorithm),
                });
        }
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task The_cache_hears_the_writes_made_after_the_last_decomposer_flush()
    {
        await CreateSeeder().SeedAsync(
            new TenantContext(TenantId, "test", "Test", true, false), days: 5, ownerSubjectId: null);

        await using var db = _db.CreateContext();
        (await db.TreatmentFoods.AnyAsync()).Should().BeTrue("the seed attributed foods to its meals");
        (await db.TempBasals.AnyAsync(t => t.ScheduledRate != null)).Should().BeTrue("the seed stamped its temps");
        (await db.StateSpans.AnyAsync()).Should().BeTrue();
        (await db.ProfileActivations.AnyAsync()).Should().BeTrue();
        _written.Models.Should().Contain([
            typeof(TreatmentFood), typeof(TempBasal), typeof(StateSpan), typeof(ProfileActivation),
        ]);
    }
}
