using FluentAssertions;
using Microsoft.EntityFrameworkCore;
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
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// <see cref="SourceOwnedDataRepository"/> on PostgreSQL under the runtime role and Row Level
/// Security: a purge and a release run as the scope's tenant reach none of another tenant's rows of
/// the same source.
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

    /// <summary>A scope that reads as <paramref name="scope"/>.</summary>
    private sealed class Factory(Func<Guid, NocturneDbContext> create, Guid scope) : INocturneDbContextFactory
    {
        public ValueTask<NocturneDbContext> CreateAsync(CancellationToken ct = default) => ValueTask.FromResult(create(scope));

        public ValueTask<NocturneDbContext> CreateForTenantAsync(Guid tenantId, CancellationToken ct = default) =>
            ValueTask.FromResult(create(tenantId));

        public ValueTask<NocturneDbContext> CreateForSubjectAsync(Guid subjectId, CancellationToken ct = default) =>
            ValueTask.FromResult(create(Guid.Empty));

        public ValueTask<NocturneDbContext> CreatePlatformAsync(CancellationToken ct = default) => ValueTask.FromResult(create(Guid.Empty));

        public ValueTask<NocturneDbContext> CreateSystemPinnedAsync(Guid tenantId, CancellationToken ct = default) =>
            ValueTask.FromResult(create(tenantId));
    }

    private NocturneDbContext Context(Guid tenant) =>
        new(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(database.AppConnectionString)
            .AddInterceptors(new TenantConnectionInterceptor())
            .Options) { TenantId = tenant };

    private SourceOwnedDataRepository Repository(Guid scope) => new(new Factory(Context, scope), new UserAuditContext());

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
    public async Task APurgeAsTenantADeletesNothingOfTenantB()
    {
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();

        var result = await Repository(a).SoftDeleteBySourceAsync(Source, $"data_source={Source}", CancellationToken.None);
        await Repository(a).AllowReimportBySourceAsync(Source, $"data_source={Source}", CancellationToken.None);

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
        [.. (await new DeviceStatusGroupRepository(new Factory(Context, tenant), NullLogger<DeviceStatusGroupRepository>.Instance)
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

        var result = await Repository(tenant).SoftDeleteBySourceAsync(Source, $"data_source={Source}", CancellationToken.None);

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

    [Fact]
    public async Task AfterTheReleaseAPurgedStatusIsStoredAgainWithItsExtras()
    {
        var tenant = await InsertTenantAsync();
        var purged = await SeedStatusAsync(tenant, Source, Source, Source);
        await Repository(tenant).SoftDeleteBySourceAsync(Source, $"data_source={Source}", CancellationToken.None);
        var extrasRepository = new DeviceStatusExtrasRepository(
            new Factory(Context, tenant), new UserAuditContext(), NullLogger<DeviceStatusExtrasRepository>.Instance);
        var resent = new DeviceStatusExtras
        {
            CorrelationId = purged,
            Timestamp = DateTime.UtcNow,
            Extras = new Dictionary<string, object?> { ["xdripjs"] = "resent" },
        };
        (await extrasRepository.BulkCreateAsync([resent], WriteOrigin.Live)).Should().BeEmpty(
            "the purge holds the status's extras against a re-upload until the release");

        await Repository(tenant).AllowReimportBySourceAsync(Source, $"data_source={Source}", CancellationToken.None);
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
