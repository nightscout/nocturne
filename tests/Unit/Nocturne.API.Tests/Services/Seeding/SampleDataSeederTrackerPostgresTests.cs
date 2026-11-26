using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Seeding;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.DeviceStatuses;
using Nocturne.Core.Constants;
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
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.API.Tests.Services.Seeding;

/// <summary>
/// Seeding trackers twice leaves the tenant's four sample definitions and the instances its device
/// schedule calls for, and the purge that rebuilds instances reaches only the seeded tenant's rows,
/// on PostgreSQL under the runtime role and Row Level Security.
/// </summary>
[Trait("Category", "Integration")]
public class SampleDataSeederTrackerPostgresTests(SampleDataSeederTrackerPostgresTests.Database database)
    : IClassFixture<SampleDataSeederTrackerPostgresTests.Database>
{
    private static readonly DateTimeOffset Instant = new(2026, 3, 14, 20, 0, 0, TimeSpan.Zero);

    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            var database = await SharedPostgres.CreateMigratedDatabaseAsync("sample_data_trackers");
            AppConnectionString = database.AppConnectionString;
            MigratorConnectionString = database.MigratorConnectionString;
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    private NocturneDbContext Context(Guid tenant) =>
        TestContexts.Context(database.AppConnectionString, tenant);

    private SuppliedContextFactory Contexts(Guid tenant) => new(() => Context(tenant));

    private async Task<Guid> InsertTenantAsync()
    {
        var tenant = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(database.MigratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
            VALUES (@id, @slug, 'tracker-seed-test', true, now(), now())
            """;
        cmd.Parameters.Add(new NpgsqlParameter("@id", tenant));
        cmd.Parameters.Add(new NpgsqlParameter("@slug", $"tracker-seed-{tenant:N}"));
        await cmd.ExecuteNonQueryAsync();
        return tenant;
    }

    private SampleDataSeeder CreateSeeder(Guid tenantId)
    {
        var contexts = Contexts(tenantId);
        var tenant = new Mock<ITenantAccessor>();
        tenant.SetupGet(t => t.TenantId).Returns(tenantId);

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
            .ReturnsAsync(new DecompositionResult());
        treatments
            .Setup(t => t.CreateTreatmentsAsync(It.IsAny<IEnumerable<Treatment>>(), WriteOrigin.Backfill, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<Treatment> batch, WriteOrigin _, CancellationToken _) => new BulkWrite<Treatment>(batch.ToList(), 0));

        var time = new FakeTimeProvider(Instant);
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        var audit = new SystemAuditContext();
        return new SampleDataSeeder(
            tenant.Object,
            contexts,
            new SubjectRepository(contexts, AccountLinkRoutes.Off),
            entries.Object,
            treatments.Object,
            sleep.Object,
            Mock.Of<IHeartRateService>(),
            Mock.Of<IStepCountService>(),
            new TrackerRepository(contexts, time),
            new AlertRuleRepository(contexts, time),
            Mock.Of<IRuleScopeClassifier>(),
            Mock.Of<IProfileWriteService>(),
            statuses.Object,
            Mock.Of<ICompressionLowDetectionService>(),
            Mock.Of<ICompressionLowService>(),
            Mock.Of<ICompressionLowRepository>(),
            new TreatmentFoodRepository(contexts),
            new StateSpanRepository(
                contexts, Mock.Of<IDuplicateLinker>(), audit, NullLogger<StateSpanRepository>.Instance),
            new ProfileActivationRepository(contexts, audit, NullLogger<ProfileActivationRepository>.Instance),
            new TempBasalRepository(
                contexts, Mock.Of<IDuplicateLinker>(), audit, NullLogger<TempBasalRepository>.Instance),
            time,
            NullLoggerFactory.Instance,
            NullLogger<SampleDataSeeder>.Instance);
    }

    private async Task<(int Definitions, int Instances)> CountsAsync(Guid tenant)
    {
        await using var db = Context(tenant);
        return (await db.TrackerDefinitions.CountAsync(), await db.TrackerInstances.CountAsync());
    }

    [Fact]
    public async Task Seeding_twice_keeps_four_definitions_and_the_same_instances_and_leaves_another_tenant_alone()
    {
        var seeded = await InsertTenantAsync();
        var other = await InsertTenantAsync();
        var otherTrackers = new TrackerRepository(Contexts(other));
        var otherDefinition = await otherTrackers.CreateDefinitionAsync(
            TrackerDefinition.Create(
                new TrackerDefinitionDraft
                {
                    Name = "Other tenant sensor",
                    Category = TrackerCategory.Sensor,
                    LifespanHours = 240,
                    TriggerEventTypes = [TreatmentTypes.SensorStart],
                    StartEventType = TreatmentTypes.SensorStart,
                    Mode = TrackerMode.Duration,
                },
                "other",
                Instant.UtcDateTime).Definition!,
            CancellationToken.None);
        await otherTrackers.StartInstanceAsync(
            otherDefinition.Id, "other", startedAt: Instant.UtcDateTime.AddDays(-2), cancellationToken: CancellationToken.None);
        await otherTrackers.StartInstanceAsync(
            otherDefinition.Id, "other", startedAt: Instant.UtcDateTime.AddDays(-1), cancellationToken: CancellationToken.None);
        var context = new TenantContext(seeded, "tracker-seed", "Tracker seed", true, false);
        var owner = Guid.NewGuid();

        await CreateSeeder(seeded).SeedAsync(context, days: 30, ownerSubjectId: owner);
        var first = await CountsAsync(seeded);
        await CreateSeeder(seeded).SeedAsync(context, days: 30, ownerSubjectId: owner);
        var second = await CountsAsync(seeded);

        first.Definitions.Should().Be(4);
        first.Instances.Should().BeGreaterThan(0);
        second.Should().Be(first);
        (await CountsAsync(other)).Should().Be((1, 2));
    }
}
