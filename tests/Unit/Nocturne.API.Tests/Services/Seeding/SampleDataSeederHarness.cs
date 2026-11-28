using Nocturne.Core.Contracts.Connectors;
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
using Nocturne.Core.Contracts.Repositories;
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
/// A <see cref="SampleDataSeeder"/> over a SQLite tenant database. The treatment, entry and device status services
/// are doubles that report nothing, so every cache report is the seeder's own.
/// </summary>
internal sealed class SampleDataSeederHarness : IDisposable
{
    internal static readonly Guid TenantId = Guid.Parse("0196a3f2-7b1e-7c3a-9d52-4e8f1a6b2c10");
    private static readonly DateTimeOffset Instant = new(2026, 3, 14, 20, 0, 0, TimeSpan.Zero);

    internal readonly SqliteTestDatabase Db = TestDbContextFactory.CreateSqliteWithTenant(TenantId);
    internal readonly RecordingLegacyReads Written = new();
    internal readonly FakeTimeProvider Time = new(Instant);

    public SampleDataSeederHarness() => Time.SetLocalTimeZone(TimeZoneInfo.Utc);

    public void Dispose() => Db.Dispose();

    internal SampleDataSeeder CreateSeeder(IDemoDataPurge? purge = null)
    {
        var contexts = Db.ContextFactory.AsNocturneFactory();
        var tenant = SettableTenantAccessor.Resolved(TenantId);

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
            tenant,
            contexts,
            new SubjectRepository(contexts, AccountLinkRoutes.Off),
            entries.Object,
            treatments.Object,
            sleep.Object,
            Mock.Of<IHeartRateService>(),
            Mock.Of<IStepCountService>(),
            Mock.Of<ITrackerRepository>(),
            new AlertRuleRepository(contexts, Time),
            Mock.Of<IRuleScopeClassifier>(),
            Mock.Of<IProfileWriteService>(),
            statuses.Object,
            Mock.Of<ICompressionLowDetectionService>(),
            Mock.Of<ICompressionLowService>(),
            Mock.Of<ICompressionLowRepository>(),
            new TreatmentFoodRepository(contexts, Written),
            new StateSpanRepository(
                contexts, Mock.Of<IDuplicateLinker>(), audit, NullLogger<StateSpanRepository>.Instance, Written),
            new ProfileActivationRepository(
                contexts, audit, NullLogger<ProfileActivationRepository>.Instance, changes: Written.AsRecordChanges()),
            new TempBasalRepository(
                contexts, Mock.Of<IDuplicateLinker>(), audit, NullLogger<TempBasalRepository>.Instance, changes: Written.AsRecordChanges()),
            new InAppNotificationRepository(contexts),
            purge ?? Mock.Of<IDemoDataPurge>(),
            Time,
            NullLoggerFactory.Instance,
            NullLogger<SampleDataSeeder>.Instance);
    }

    private async Task DecomposeAsync(IReadOnlyList<Treatment> batch)
    {
        await using var db = Db.CreateContext();
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
}
