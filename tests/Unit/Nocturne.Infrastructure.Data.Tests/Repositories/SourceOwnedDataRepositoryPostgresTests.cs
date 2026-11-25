using System.Data.Common;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models.Connectors;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Nocturne.Tests.Shared.Mocks;
using Npgsql;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// <see cref="SourceOwnedDataRepository"/> on PostgreSQL under the runtime role and Row Level
/// Security: a purge and a release run as the scope's tenant reach none of another tenant's rows of
/// the same source, and a source's stats count each row either handle names once, off indexes.
/// </summary>
[Trait("Category", "Integration")]
public class SourceOwnedDataRepositoryPostgresTests(SourceOwnedDataRepositoryPostgresTests.Database database)
    : IClassFixture<SourceOwnedDataRepositoryPostgresTests.Database>
{
    private const string Source = "loop-connector";
    private const string OtherSource = "other-connector";

    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            var database = await SharedPostgres.CreateMigratedDatabaseAsync("source_owned_data_repo");
            AppConnectionString = database.AppConnectionString;
            MigratorConnectionString = database.MigratorConnectionString;
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    private NocturneDbContext Context(Guid tenant) => Context(tenant, []);

    private NocturneDbContext Context(Guid tenant, params IInterceptor[] interceptors) =>
        TestContexts.Context(database.AppConnectionString, tenant, interceptors);

    /// <summary>The queries a context runs, with their parameters.</summary>
    private sealed class QueryCapture : DbCommandInterceptor
    {
        public List<(string Sql, NpgsqlParameter[] Parameters)> Queries { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Queries.Add((command.CommandText, [.. command.Parameters.Cast<NpgsqlParameter>().Select(p => p.Clone())]));
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private SourceOwnedDataRepository Repository(Guid scope) => new(new SuppliedContextFactory(() => Context(scope)), new UserAuditContext());

    private async Task<Guid> InsertTenantAsync()
    {
        var tenant = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(database.MigratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
            VALUES (@id, @slug, 'source-owned-data-test', true, now(), now())
            """;
        cmd.Parameters.Add(new NpgsqlParameter("@id", tenant));
        cmd.Parameters.Add(new NpgsqlParameter("@slug", $"source-{tenant:N}"));
        await cmd.ExecuteNonQueryAsync();
        return tenant;
    }

    private async Task<Guid> SeedTenantAsync()
    {
        var tenant = await InsertTenantAsync();
        await using var db = Context(tenant);
        db.Boluses.Add(new BolusEntity
        {
            Id = Guid.CreateVersion7(), TenantId = tenant, LegacyId = $"bolus-{tenant:N}",
            DataSource = Source, Timestamp = DateTime.UtcNow, Insulin = 1,
        });
        db.BasalInjections.Add(new BasalInjectionEntity
        {
            Id = Guid.CreateVersion7(), TenantId = tenant, DataSource = Source, Timestamp = DateTime.UtcNow, Units = 10,
        });
        db.BasalInjections.Add(new BasalInjectionEntity
        {
            Id = Guid.CreateVersion7(), TenantId = tenant, DataSource = OtherSource, Timestamp = DateTime.UtcNow, Units = 10,
        });
        db.PumpSnapshots.Add(new PumpSnapshotEntity
        {
            Id = Guid.CreateVersion7(), TenantId = tenant, DataSource = Source, Timestamp = DateTime.UtcNow,
        });
        db.UploaderSnapshots.Add(new UploaderSnapshotEntity
        {
            Id = Guid.CreateVersion7(), TenantId = tenant, DataSource = Source, Timestamp = DateTime.UtcNow,
        });
        db.StateSpans.Add(new StateSpanEntity
        {
            Id = Guid.CreateVersion7(), TenantId = tenant, Source = Source,
            Category = "PumpMode", State = "Automatic", StartTimestamp = DateTime.UtcNow,
        });
        db.TherapySettings.Add(new TherapySettingsEntity
        {
            Id = Guid.CreateVersion7(), TenantId = tenant, DataSource = Source, Timestamp = DateTime.UtcNow,
            ProfileName = "Day",
        });
        await db.SaveChangesAsync();
        return tenant;
    }

    [Fact]
    public async Task HoldingRowsIsReadByTheDataSourceAloneAndForTheScopesTenantOnly()
    {
        var holder = await SeedTenantAsync();
        var empty = await InsertTenantAsync();
        var deviceOnly = await InsertTenantAsync();
        var activityOnly = await InsertTenantAsync();
        await using (var db = Context(deviceOnly))
        {
            db.HeartRates.Add(new HeartRateEntity
            {
                Id = Guid.CreateVersion7(), TenantId = deviceOnly, Device = Source, Timestamp = DateTime.UtcNow.AddDays(-90), Bpm = 60,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = Context(activityOnly))
        {
            db.HeartRates.Add(new HeartRateEntity
            {
                Id = Guid.CreateVersion7(), TenantId = activityOnly, DataSource = Source, Timestamp = DateTime.UtcNow.AddDays(-90), Bpm = 60,
            });
            await db.SaveChangesAsync();
        }

        (await Repository(holder).HoldsRowsByDataSourceAsync(Source, CancellationToken.None)).Should().BeTrue();
        (await Repository(holder).HoldsRowsByDataSourceAsync("never-written", CancellationToken.None)).Should().BeFalse();
        (await Repository(empty).HoldsRowsByDataSourceAsync(Source, CancellationToken.None)).Should().BeFalse();
        (await Repository(deviceOnly).HoldsRowsByDataSourceAsync(Source, CancellationToken.None)).Should().BeFalse();
        (await Repository(activityOnly).HoldsRowsByDataSourceAsync(Source, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task APurgeAsTenantADeletesNothingOfTenantB()
    {
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();

        var result = await Repository(a).SoftDeleteAsync(RecordSelector.ForSource(Source), CancellationToken.None);
        await Repository(a).AllowReimportAsync(RecordSelector.ForSource(Source), CancellationToken.None);

        result.DeletedCounts.Should().BeEquivalentTo(new Dictionary<string, long>
        {
            [SourceDataTypes.Boluses] = 1,
            [SourceDataTypes.BasalInjections] = 1,
            [SourceDataTypes.DeviceStatus] = 2,
            [SourceDataTypes.StateSpans] = 1,
            [SourceDataTypes.Profiles] = 1,
        });
        (await Repository(a).CountsBySourceAsync(Source, CancellationToken.None)).Should().BeEmpty();
        (await Repository(b).CountsBySourceAsync(Source, CancellationToken.None)).Should().BeEquivalentTo(
            new Dictionary<string, long>
            {
                [SourceDataTypes.Boluses] = 1,
                [SourceDataTypes.BasalInjections] = 1,
                [SourceDataTypes.DeviceStatus] = 2,
                [SourceDataTypes.StateSpans] = 1,
                [SourceDataTypes.Profiles] = 1,
            });
        await using (var db = Context(b))
        {
            (await db.Boluses.IgnoreQueryFilters().SingleAsync()).DeletedAt.Should().BeNull();
            (await db.BasalInjections.IgnoreQueryFilters().ToListAsync()).Should().OnlyContain(e => e.DeletedAt == null);
            (await db.PumpSnapshots.IgnoreQueryFilters().SingleAsync()).DeletedAt.Should().BeNull();
            (await db.UploaderSnapshots.IgnoreQueryFilters().SingleAsync()).DeletedAt.Should().BeNull();
        }
        await using (var db = Context(a))
        {
            var bolus = await db.Boluses.IgnoreQueryFilters().SingleAsync();
            bolus.DeletedAt.Should().NotBeNull();
            db.Entry(bolus).Property<bool>("DeletedByUser").CurrentValue.Should().BeFalse("the release lifts the user tombstone so the source may send the record again");

            var injections = await db.BasalInjections.IgnoreQueryFilters().ToListAsync();
            injections.Single(e => e.DataSource == Source).DeletedAt.Should().NotBeNull();
            injections.Single(e => e.DataSource == OtherSource).DeletedAt.Should().BeNull("another source's rows stay");
            (await db.PumpSnapshots.IgnoreQueryFilters().SingleAsync()).DeletedAt.Should().NotBeNull();
            (await db.UploaderSnapshots.IgnoreQueryFilters().SingleAsync()).DeletedAt.Should().NotBeNull();
            foreach (var entity in new object[] { injections.Single(e => e.DataSource == Source), await db.PumpSnapshots.IgnoreQueryFilters().SingleAsync(), await db.UploaderSnapshots.IgnoreQueryFilters().SingleAsync() })
                db.Entry(entity).Property("DeletedByUser").CurrentValue.Should().Be(false);
        }
    }

    /// <summary>
    /// One decomposed status: an extras row, and a snapshot from each source given, in the order APS,
    /// pump, uploader. A null source leaves that snapshot out.
    /// </summary>
    private async Task<Guid> SeedStatusAsync(Guid tenant, params string?[] sources)
    {
        var correlationId = Guid.CreateVersion7();
        var timestamp = DateTime.UtcNow.AddMinutes(-Random.Shared.Next(1, 600));
        await using var db = Context(tenant);
        db.DeviceStatusExtras.Add(new DeviceStatusExtrasEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant,
            CorrelationId = correlationId,
            Timestamp = timestamp,
            ExtrasJson = """{"xdripjs":{"state":6}}""",
        });
        if (sources.ElementAtOrDefault(0) is not null)
        {
            db.ApsSnapshots.Add(new ApsSnapshotEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenant,
                CorrelationId = correlationId,
                DataSource = sources[0],
                AidAlgorithm = "OpenAps",
                Timestamp = timestamp,
            });
        }
        if (sources.ElementAtOrDefault(1) is not null)
        {
            db.PumpSnapshots.Add(new PumpSnapshotEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenant,
                CorrelationId = correlationId,
                DataSource = sources[1],
                Timestamp = timestamp,
            });
        }
        if (sources.ElementAtOrDefault(2) is not null)
        {
            db.UploaderSnapshots.Add(new UploaderSnapshotEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenant,
                CorrelationId = correlationId,
                DataSource = sources[2],
                Timestamp = timestamp,
            });
        }
        await db.SaveChangesAsync();
        return correlationId;
    }

    /// <summary>The correlation ids of the statuses the v1 and v3 devicestatus reads serve.</summary>
    private async Task<IReadOnlyList<Guid>> ServedStatusesAsync(Guid tenant) =>
        [.. (await new DeviceStatusGroupRepository(new SuppliedContextFactory(() => Context(tenant)), NullLogger<DeviceStatusGroupRepository>.Instance)
                .GetAsync(from: null, to: null, device: null, limit: 100, offset: 0, descending: true))
            .Select(g => g.Extras!.CorrelationId)];

    [Fact]
    public async Task APurgeDeletesTheExtrasOfEveryStatusItLeavesWithNoLiveSnapshot()
    {
        var tenant = await InsertTenantAsync();
        var other = await InsertTenantAsync();
        var purged = await SeedStatusAsync(tenant, Source, Source, Source);
        var apsOnly = await SeedStatusAsync(tenant, Source);
        var otherSources = await SeedStatusAsync(tenant, OtherSource, OtherSource, OtherSource);
        var keptByAps = await SeedStatusAsync(tenant, OtherSource, Source);
        var keptByPump = await SeedStatusAsync(tenant, Source, OtherSource);
        var keptByUploader = await SeedStatusAsync(tenant, Source, Source, OtherSource);
        var unsnapshotted = await SeedStatusAsync(tenant);
        var uploaderOnly = await SeedStatusAsync(tenant, null, null, Source);
        var pumpOnly = await SeedStatusAsync(tenant, null, Source);
        var otherTenants = await SeedStatusAsync(other, Source, Source, Source);

        var result = await Repository(tenant).SoftDeleteAsync(RecordSelector.ForSource(Source), CancellationToken.None);

        (await ServedStatusesAsync(tenant)).Should().BeEquivalentTo([otherSources, keptByAps, keptByPump, keptByUploader, unsnapshotted]);
        (await ServedStatusesAsync(other)).Should().Equal(otherTenants);
        result.DeletedCounts.Should().Equal(new Dictionary<string, long> { [SourceDataTypes.DeviceStatus] = 10 });
        await using var db = Context(tenant);
        var extras = await db.DeviceStatusExtras.IgnoreQueryFilters().ToDictionaryAsync(e => e.CorrelationId);
        extras[purged].DeletedAt.Should().NotBeNull();
        extras[apsOnly].DeletedAt.Should().NotBeNull();
        extras[uploaderOnly].DeletedAt.Should().NotBeNull();
        extras[pumpOnly].DeletedAt.Should().NotBeNull();
        extras[keptByAps].DeletedAt.Should().BeNull("another source's live APS snapshot keeps the status live");
        extras[keptByPump].DeletedAt.Should().BeNull("another source's live pump snapshot keeps the status live");
        extras[keptByUploader].DeletedAt.Should().BeNull("another source's live uploader snapshot keeps the status live");
        db.Entry(extras[purged]).Property<bool>("DeletedByUser").CurrentValue.Should().BeTrue(
            "a user's purge holds the extras against a re-upload as it holds the snapshots");
        extras[purged].SysUpdatedAt.Should().BeAfter(extras[unsnapshotted].SysUpdatedAt,
            "a v3 history client learns of the delete through the stamp");
        (await db.MutationAuditLog.CountAsync(a => a.EntityType == nameof(DeviceStatusExtras) && a.Action == "bulk_delete"))
            .Should().Be(1);
    }

    /// <summary>
    /// A purge deletes the source's sensor state reports and announces <see cref="SensorStateReport"/>,
    /// which states the sensor's state to alerts; a purge of another source leaves them. A report is a
    /// row of its status's source as a snapshot is, so a status it alone names has its extras deleted
    /// too. The extras are not announced, since nothing reads them as a record type.
    /// </summary>
    [Fact]
    public async Task APurgeDeletesTheSourcesSensorStateReportsAndAnnouncesThem()
    {
        var tenant = await InsertTenantAsync();
        var snapshotted = await SeedStatusAsync(tenant, Source, Source, Source);
        var reportOnly = await SeedStatusAsync(tenant);
        await using (var db = Context(tenant))
        {
            foreach (var correlationId in new[] { snapshotted, reportOnly })
            {
                db.SensorStateReports.Add(new SensorStateReportEntity
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenant,
                    CorrelationId = correlationId,
                    DataSource = Source,
                    Device = "CareLink MiniMed 780G",
                    Timestamp = DateTime.UtcNow,
                    State = nameof(Nocturne.Core.Models.Alerts.SensorState.WarmingUp),
                    ReportedField = SensorStateReport.CareLinkField,
                    ReportedValue = "WARM_UP",
                });
            }

            await db.SaveChangesAsync();
        }
        var changes = new RecordingRecordChanges();
        var repository = new SourceOwnedDataRepository(new SuppliedContextFactory(() => Context(tenant)), new UserAuditContext(), changes);

        await repository.SoftDeleteAsync(RecordSelector.ForSource(OtherSource), CancellationToken.None);
        var untouched = changes.Changes.Select(c => c.RecordType).ToList();
        await using (var db = Context(tenant))
            (await db.SensorStateReports.CountAsync()).Should().Be(2);
        await repository.SoftDeleteAsync(RecordSelector.ForSource(Source), CancellationToken.None);

        untouched.Should().NotContain(typeof(SensorStateReport));
        var announced = changes.Changes.Select(c => c.RecordType).ToList();
        announced.Should().Contain(typeof(SensorStateReport));
        announced.Should().NotContain(typeof(DeviceStatusExtras));
        await using (var db = Context(tenant))
        {
            (await db.SensorStateReports.CountAsync()).Should().Be(0);
            var extras = await db.DeviceStatusExtras.IgnoreQueryFilters().ToDictionaryAsync(e => e.CorrelationId);
            extras[snapshotted].DeletedAt.Should().NotBeNull();
            extras[reportOnly].DeletedAt.Should().NotBeNull("the status's report is a row of the source");
        }
    }

    [Fact]
    public async Task AfterTheReleaseAPurgedStatusIsStoredAgainWithItsExtras()
    {
        var tenant = await InsertTenantAsync();
        var purged = await SeedStatusAsync(tenant, Source, Source, Source);
        await Repository(tenant).SoftDeleteAsync(RecordSelector.ForSource(Source), CancellationToken.None);
        var extrasRepository = new DeviceStatusExtrasRepository(
            new SuppliedContextFactory(() => Context(tenant)), new UserAuditContext(), NullLogger<DeviceStatusExtrasRepository>.Instance);
        var resent = new DeviceStatusExtras
        {
            CorrelationId = purged,
            Timestamp = DateTime.UtcNow,
            Extras = new Dictionary<string, object?> { ["xdripjs"] = "resent" },
        };
        (await extrasRepository.BulkCreateAsync([resent], WriteOrigin.Live)).Should().BeEmpty(
            "the purge holds the status's extras against a re-upload until the release");

        await Repository(tenant).AllowReimportAsync(RecordSelector.ForSource(Source), CancellationToken.None);
        (await extrasRepository.BulkCreateAsync([resent], WriteOrigin.Live)).Should().ContainSingle();
        await using (var db = Context(tenant))
        {
            db.ApsSnapshots.Add(new ApsSnapshotEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenant,
                CorrelationId = purged,
                DataSource = Source,
                AidAlgorithm = "OpenAps",
                Timestamp = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        (await ServedStatusesAsync(tenant)).Should().Equal(purged);
    }

    private async Task SeedHandlesAsync(Guid tenant, DateTime now)
    {
        await using var db = Context(tenant);
        void Bolus(string? dataSource, string? device, DateTime at, DateTime? deletedAt = null) =>
            db.Boluses.Add(new BolusEntity
            {
                Id = Guid.CreateVersion7(), TenantId = tenant, LegacyId = $"bolus-{Guid.NewGuid():N}",
                DataSource = dataSource, Device = device, Timestamp = at, Insulin = 1, DeletedAt = deletedAt,
            });
        void Glucose(string? dataSource, string? device, DateTime at) =>
            db.SensorGlucose.Add(new SensorGlucoseEntity
            {
                Id = Guid.CreateVersion7(), TenantId = tenant, DataSource = dataSource, Device = device,
                Timestamp = at, Mgdl = 100,
            });

        Bolus(Source, Source, now.AddHours(-1));
        Bolus(Source, "phone", now.AddDays(-2));
        Bolus(null, Source, now.AddDays(-3));
        Bolus("aaa-connector", Source, now.AddMinutes(-30));
        Bolus("zzz-connector", Source, now.AddDays(-5));
        Bolus(OtherSource, "phone", now);
        Bolus(null, null, now.AddDays(-9));
        Bolus(Source, Source, now.AddDays(-20), deletedAt: now);

        Glucose(Source, Source, now.AddMinutes(-10));
        Glucose(null, Source, now.AddDays(-3));
        Glucose("zzz-connector", Source, now.AddHours(-1));
        Glucose(OtherSource, OtherSource, now.AddDays(-30));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task StatsCountEveryLiveRowEitherHandleNamesOnce()
    {
        var tenant = await InsertTenantAsync();
        var other = await InsertTenantAsync();
        var now = DateTime.UtcNow;
        await SeedHandlesAsync(tenant, now);
        await SeedHandlesAsync(other, now.AddDays(-100));
        var repository = Repository(tenant);

        var stats = await repository.StatsBySourceAsync(Source, now.AddHours(-24), CancellationToken.None);

        stats.Single(s => s.DataType == SourceDataTypes.Boluses).Should().Be(new SourceTypeStats(SourceDataTypes.Boluses, 5, 2));
        var glucose = stats.Single(s => s.DataType == SourceDataTypes.Glucose);
        (glucose.Count, glucose.CountLast24Hours).Should().Be((3, 2));
        glucose.Latest.Should().BeCloseTo(now.AddMinutes(-10), TimeSpan.FromMilliseconds(1));
        glucose.Oldest.Should().BeCloseTo(now.AddDays(-3), TimeSpan.FromMilliseconds(1));
        stats.Where(s => s.DataType is not (SourceDataTypes.Boluses or SourceDataTypes.Glucose))
            .Should().OnlyContain(s => s.Count == 0);
        (await repository.LatestTreatmentTimestampAsync(Source, CancellationToken.None))
            .Should().BeCloseTo(now.AddMinutes(-30), TimeSpan.FromMilliseconds(1));
        (await repository.OldestTreatmentTimestampAsync(Source, CancellationToken.None))
            .Should().BeCloseTo(now.AddDays(-5), TimeSpan.FromMilliseconds(1));
        (await repository.LatestTreatmentTimestampAsync("nobody", CancellationToken.None)).Should().BeNull();
    }

    /// <summary>
    /// Rows of <c>boluses</c> and <c>sensor_glucose</c> whose handles <paramref name="handles"/> picks, then
    /// an ANALYZE so the planner sizes them from real statistics.
    /// </summary>
    private async Task SeedBulkAsync(Guid tenant, int count, Func<int, (string? DataSource, string Device)> handles)
    {
        var now = DateTime.UtcNow;
        await using (var db = Context(tenant))
        {
            for (var i = 0; i < count; i++)
            {
                var (dataSource, device) = handles(i);
                db.Boluses.Add(new BolusEntity
                {
                    Id = Guid.CreateVersion7(), TenantId = tenant, LegacyId = $"bulk-{i}",
                    DataSource = dataSource, Device = device, Timestamp = now.AddMinutes(-i), Insulin = 1,
                });
                db.SensorGlucose.Add(new SensorGlucoseEntity
                {
                    Id = Guid.CreateVersion7(), TenantId = tenant, DataSource = dataSource, Device = device,
                    Timestamp = now.AddMinutes(-i), Mgdl = 100,
                });
            }

            await db.SaveChangesAsync();
        }

        await using var conn = new NpgsqlConnection(database.MigratorConnectionString);
        await conn.OpenAsync();
        await using var analyze = new NpgsqlCommand("ANALYZE boluses, sensor_glucose", conn);
        await analyze.ExecuteNonQueryAsync();
    }

    private Task SeedOtherDevicesAsync(Guid tenant, int count) =>
        SeedBulkAsync(tenant, count, i => (i % 2 == 0 ? OtherSource : null, $"phone-{i % 7}"));

    private Task SeedCommonSourceAsync(Guid tenant, int count) =>
        SeedBulkAsync(tenant, count, _ => (Source, Source));

    [Fact]
    public async Task CountsCountEveryLiveRowEitherHandleNamesOnceForTheScopesTenantOnly()
    {
        var tenant = await InsertTenantAsync();
        var other = await InsertTenantAsync();
        var now = DateTime.UtcNow;
        await SeedHandlesAsync(tenant, now);
        await SeedHandlesAsync(other, now.AddDays(-100));

        var counts = await Repository(tenant).CountsBySourceAsync(Source, CancellationToken.None);

        counts.Should().BeEquivalentTo(new Dictionary<string, long>
        {
            [SourceDataTypes.Boluses] = 5,
            [SourceDataTypes.Glucose] = 3,
        });
        (await Repository(tenant).CountsBySourceAsync("nobody", CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task TheSeededCountedTablesAreReadByTheDeviceIndexForASourceTheTenantHolds()
    {
        var tenant = await InsertTenantAsync();
        await SeedHandlesAsync(tenant, DateTime.UtcNow);
        await SeedOtherDevicesAsync(tenant, 3000);

        await AssertCountedTablePlansAsync(tenant, Source, "nobody");
    }

    [Fact]
    public async Task TheSeededCountedTablesAreReadByTheDeviceIndexForASourceCommonToOtherTenantsAndAbsentFromTheTenant()
    {
        var tenant = await InsertTenantAsync();
        await SeedOtherDevicesAsync(tenant, 3000);
        await SeedCommonSourceAsync(await InsertTenantAsync(), 6000);

        await AssertCountedTablePlansAsync(tenant, Source);
    }

    private async Task AssertCountedTablePlansAsync(Guid tenant, params string[] sources)
    {
        var capture = new QueryCapture();
        var repository = new SourceOwnedDataRepository(
            new SuppliedContextFactory(() => Context(tenant, capture)), new UserAuditContext());

        foreach (var source in sources)
        {
            await repository.StatsBySourceAsync(source, DateTime.UtcNow.AddHours(-24), CancellationToken.None);
            await repository.LatestTreatmentTimestampAsync(source, CancellationToken.None);
            await repository.OldestTreatmentTimestampAsync(source, CancellationToken.None);
            await repository.CountsBySourceAsync(source, CancellationToken.None);
        }

        capture.Queries.Should().NotBeEmpty();
        await using var db = Context(tenant);
        await db.Database.OpenConnectionAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        foreach (var (sql, parameters) in capture.Queries)
        {
            await using var explain = new NpgsqlCommand($"EXPLAIN (FORMAT JSON) {sql}", connection);
            explain.Parameters.AddRange(parameters);
            var plan = (string)(await explain.ExecuteScalarAsync())!;

            ScansOf(JsonNode.Parse(plan)![0]!["Plan"]!)
                .Where(scan => (string)scan["Relation Name"]! is "boluses" or "sensor_glucose")
                .Should().OnlyContain(scan => BoundBySource(scan),
                    "a read of a table with statistics is a range of an index on its source handles, not of the tenant's rows:\n{0}\n{1}",
                    sql, plan);
        }
    }

    /// <summary>The plan nodes that read a table.</summary>
    private static IEnumerable<JsonNode> ScansOf(JsonNode node) =>
        (node["Relation Name"] is null ? [] : new[] { node })
            .Concat(node["Plans"]?.AsArray().SelectMany(child => ScansOf(child!)) ?? []);

    /// <summary>Whether a scan reads only rows an index condition on a source handle bounds.</summary>
    private static bool BoundBySource(JsonNode node) => (string)node["Node Type"]! switch
    {
        "Index Scan" or "Index Only Scan" or "Bitmap Index Scan" =>
            node["Index Cond"]?.GetValue<string>() is { } cond && (cond.Contains("source") || cond.Contains("device")),
        "BitmapAnd" => node["Plans"]!.AsArray().Any(child => BoundBySource(child!)),
        "BitmapOr" => node["Plans"]!.AsArray().All(child => BoundBySource(child!)),
        "Bitmap Heap Scan" => BoundBySource(node["Plans"]![0]!),
        _ => false,
    };

    /// <summary>
    /// A bolus, a state span, a therapy setting and a status of APS and pump snapshots with its extras,
    /// all under <see cref="Source"/> and written by <paramref name="writer"/>. Returns the status's
    /// correlation id.
    /// </summary>
    private async Task<Guid> SeedWrittenAsync(Guid tenant, Guid? writer)
    {
        var correlationId = Guid.CreateVersion7();
        var now = DateTime.UtcNow;
        await using var db = Context(tenant);
        db.Boluses.Add(new BolusEntity
        {
            Id = Guid.CreateVersion7(), TenantId = tenant, LegacyId = $"bolus-{correlationId:N}",
            DataSource = Source, Timestamp = now, Insulin = 1, WriterGrantId = writer,
        });
        db.StateSpans.Add(new StateSpanEntity
        {
            Id = Guid.CreateVersion7(), TenantId = tenant, Source = Source,
            Category = "PumpMode", State = "Automatic", StartTimestamp = now, WriterGrantId = writer,
        });
        db.TherapySettings.Add(new TherapySettingsEntity
        {
            Id = Guid.CreateVersion7(), TenantId = tenant, DataSource = Source, Timestamp = now,
            ProfileName = $"Day-{correlationId:N}", WriterGrantId = writer,
        });
        db.DeviceStatusExtras.Add(new DeviceStatusExtrasEntity
        {
            Id = Guid.CreateVersion7(), TenantId = tenant, CorrelationId = correlationId, Timestamp = now,
            ExtrasJson = """{"xdripjs":{"state":6}}""",
        });
        db.ApsSnapshots.Add(new ApsSnapshotEntity
        {
            Id = Guid.CreateVersion7(), TenantId = tenant, CorrelationId = correlationId, DataSource = Source,
            AidAlgorithm = "OpenAps", Timestamp = now, WriterGrantId = writer,
        });
        db.PumpSnapshots.Add(new PumpSnapshotEntity
        {
            Id = Guid.CreateVersion7(), TenantId = tenant, CorrelationId = correlationId, DataSource = Source,
            Timestamp = now, WriterGrantId = writer,
        });
        await db.SaveChangesAsync();
        return correlationId;
    }

    [Fact]
    public async Task AWritersPurgeDeletesOnlyTheRowsItsGrantsWroteUnderAHandleAnotherWriterShares()
    {
        var tenant = await InsertTenantAsync();
        Guid writer = Guid.NewGuid(), sharer = Guid.NewGuid();
        var written = await SeedWrittenAsync(tenant, writer);
        var shared = await SeedWrittenAsync(tenant, sharer);
        var unwritten = await SeedWrittenAsync(tenant, writer: null);

        var result = await Repository(tenant).SoftDeleteAsync(RecordSelector.ForWriters([writer]), CancellationToken.None);

        result.DeletedCounts.Should().BeEquivalentTo(new Dictionary<string, long>
        {
            [SourceDataTypes.Boluses] = 1,
            [SourceDataTypes.DeviceStatus] = 2,
            [SourceDataTypes.StateSpans] = 1,
            [SourceDataTypes.Profiles] = 1,
        });
        (await ServedStatusesAsync(tenant)).Should().BeEquivalentTo([shared, unwritten]);
        await using var db = Context(tenant);
        (await db.Boluses.IgnoreQueryFilters().Where(b => b.DeletedAt != null).Select(b => b.WriterGrantId).ToListAsync())
            .Should().Equal(writer);
        (await db.StateSpans.IgnoreQueryFilters().Where(s => s.DeletedAt != null).Select(s => s.WriterGrantId).ToListAsync())
            .Should().Equal(writer);
        (await db.TherapySettings.IgnoreQueryFilters().Where(t => t.DeletedAt != null).Select(t => t.WriterGrantId).ToListAsync())
            .Should().Equal(writer);
        var extras = await db.DeviceStatusExtras.IgnoreQueryFilters().ToDictionaryAsync(e => e.CorrelationId);
        extras[written].DeletedAt.Should().NotBeNull("its writer's purge leaves the status with no live snapshot");
        db.Entry(extras[written]).Property<bool>("DeletedByUser").CurrentValue.Should().BeTrue();
        extras[shared].DeletedAt.Should().BeNull();
        extras[unwritten].DeletedAt.Should().BeNull();
        var bolus = await db.Boluses.IgnoreQueryFilters().SingleAsync(b => b.WriterGrantId == writer);
        db.Entry(bolus).Property<bool>("DeletedByUser").CurrentValue.Should().BeTrue(
            "a user's delete holds the record against a re-upload");
        (await db.MutationAuditLog.Where(a => a.Action == "bulk_delete").Select(a => a.ChangesJson).ToListAsync())
            .Should().NotBeEmpty().And.OnlyContain(json => json!.Contains($"writer_grant_id={writer}"));
    }

    [Fact]
    public async Task AWritersReleaseLiftsOnlyTheTombstonesOfItsGrants()
    {
        var tenant = await InsertTenantAsync();
        Guid writer = Guid.NewGuid(), sharer = Guid.NewGuid();
        var written = await SeedWrittenAsync(tenant, writer);
        var shared = await SeedWrittenAsync(tenant, sharer);
        await Repository(tenant).SoftDeleteAsync(RecordSelector.ForWriters([writer, sharer]), CancellationToken.None);

        await Repository(tenant).AllowReimportAsync(RecordSelector.ForWriters([writer]), CancellationToken.None);

        await using var db = Context(tenant);
        bool HeldAgainstReimport(object entity) => (bool)db.Entry(entity).Property("DeletedByUser").CurrentValue!;
        var boluses = await db.Boluses.IgnoreQueryFilters().ToDictionaryAsync(b => b.WriterGrantId!.Value);
        boluses.Values.Should().OnlyContain(b => b.DeletedAt != null, "the release leaves the rows deleted");
        HeldAgainstReimport(boluses[writer]).Should().BeFalse();
        HeldAgainstReimport(boluses[sharer]).Should().BeTrue("another writer's tombstone under the same handle stays");
        var extras = await db.DeviceStatusExtras.IgnoreQueryFilters().ToDictionaryAsync(e => e.CorrelationId);
        HeldAgainstReimport(extras[written]).Should().BeFalse();
        HeldAgainstReimport(extras[shared]).Should().BeTrue();
    }

    private sealed class UserAuditContext : IAuditContext
    {
        public Guid? SubjectId => Guid.Empty;
        public string? SubjectName => "tester";
        public string? AuthType => "SessionCookie";
        public string? IpAddress => "127.0.0.1";
        public Guid? TokenId => null;
        public string? TraceId => null;
        public string? Endpoint => "DELETE /api/v4/data-sources";
        public bool IsSystem => false;
    }
}
