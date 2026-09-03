using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Nocturne.Tests.Shared.Mocks;

namespace Nocturne.Infrastructure.Data.Tests.Repositories.V4;

/// <remarks>
/// SQLite rather than the in-memory provider: <c>EnsureCreated</c> builds the partial unique index
/// filtered to <c>deleted_at IS NULL</c>, so the insert past the tombstone is proven legal against a
/// store that enforces it.
/// </remarks>
[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
public class SyncUpsertTombstoneTests : IDisposable
{
    private const string DataSource = "aaps";
    private const string SyncIdentifier = "sync-1";
    private const string LiveSyncIdentifier = "sync-2";
    private const string LegacyId = "5f2b1c0000000000000000aa";

    private static readonly Guid Tenant = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly DateTime T0 = new(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime DeletedOn = new(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc);

    private readonly DbConnection _connection;
    private readonly DbContextOptions<NocturneDbContext> _options;
    private readonly NocturneDbContext _context;

    public SyncUpsertTombstoneTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .EnableSensitiveDataLogging()
            .Options;

        using (var seed = new NocturneDbContext(_options) { TenantId = Tenant })
        {
            seed.Database.EnsureCreated();
            seed.Tenants.Add(new TenantEntity { Id = Tenant, Slug = "tenant-a" });
            seed.SaveChanges();
        }

        _context = new NocturneDbContext(_options) { TenantId = Tenant };
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private NocturneDbContext NewContext() => new(_options) { TenantId = Tenant };

    private Guid SeedRow<TEntity>(TEntity entity, DateTime? deletedAt, bool deletedByUser)
        where TEntity : class, IV4TimeSeriesEntity
    {
        entity.Id = Guid.CreateVersion7();
        entity.TenantId = Tenant;
        entity.Timestamp = T0;
        entity.DeletedAt = deletedAt;

        using var ctx = NewContext();
        var entry = ctx.Set<TEntity>().Add(entity);
        entry.Property("DeletedByUser").CurrentValue = deletedByUser;
        ctx.SaveChanges();
        return entity.Id;
    }

    private Guid SeedSyncKeyed<TEntity>(TEntity entity, DateTime? deletedAt, bool deletedByUser)
        where TEntity : class, IV4TimeSeriesEntity, ISyncDedupable
    {
        entity.DataSource = DataSource;
        entity.SyncIdentifier = SyncIdentifier;
        return SeedRow(entity, deletedAt, deletedByUser);
    }

    private Guid SeedTombstone<TEntity>(TEntity entity, bool deletedByUser)
        where TEntity : class, IV4TimeSeriesEntity, ISyncDedupable
        => SeedSyncKeyed(entity, DeletedOn, deletedByUser);

    private Guid SeedLiveRow<TEntity>(TEntity entity)
        where TEntity : class, IV4TimeSeriesEntity, ISyncDedupable
        => SeedSyncKeyed(entity, deletedAt: null, deletedByUser: false);

    private Guid SeedLegacyIdRow<TEntity>(TEntity entity, DateTime? deletedAt, bool deletedByUser)
        where TEntity : class, IV4TimeSeriesEntity
    {
        entity.LegacyId = LegacyId;
        return SeedRow(entity, deletedAt, deletedByUser);
    }

    private BolusRepository NewBolusRepository(RecordingV4RecordBroadcaster<Bolus> broadcaster) =>
        new(new TestTenantDbContextFactory(_context),
            new Mock<IDeduplicationService>().Object,
            new Mock<IAuditContext>().Object,
            NullLogger<BolusRepository>.Instance,
            broadcaster);

    private ApsSnapshotRepository NewApsSnapshotRepository(
        RecordingV4RecordBroadcaster<ApsSnapshot> broadcaster) =>
        new(new TestTenantDbContextFactory(_context),
            new Mock<IAuditContext>().Object,
            NullLogger<ApsSnapshotRepository>.Instance,
            broadcaster);

    private MeterGlucoseRepository NewMeterGlucoseRepository(
        RecordingV4RecordBroadcaster<MeterGlucose> broadcaster) =>
        new(new TestTenantDbContextFactory(_context),
            new Mock<IAuditContext>().Object,
            NullLogger<MeterGlucoseRepository>.Instance,
            broadcaster);

    private static Bolus ReuploadedBolus(double insulin) => new()
    {
        Timestamp = T0,
        DataSource = DataSource,
        SyncIdentifier = SyncIdentifier,
        Insulin = insulin,
    };

    private async Task AssertInsertedPastTombstoneAsync<TEntity>(
        Guid tombstoneId, Func<TEntity, double> value, double deletedValue, double reuploaded)
        where TEntity : class, IV4TimeSeriesEntity
    {
        await using var verify = NewContext();

        var tombstone = await verify.Set<TEntity>().IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(e => e.Id == tombstoneId);
        tombstone.DeletedAt.Should().Be(DeletedOn, "the tombstone row is left untouched");
        value(tombstone).Should().Be(deletedValue, "the re-upload must not be written into the tombstone");

        var live = await verify.Set<TEntity>().AsNoTracking().SingleAsync();
        live.Id.Should().NotBe(tombstoneId);
        value(live).Should().Be(reuploaded, "a filtered read returns the re-uploaded record");
    }

    private async Task AssertTombstoneStillHoldsTheKeyAsync<TEntity>(
        Guid tombstoneId, Func<TEntity, double> value, double deletedValue)
        where TEntity : class, IV4TimeSeriesEntity
    {
        await using var verify = NewContext();

        var row = (await verify.Set<TEntity>().IgnoreQueryFilters().AsNoTracking().ToListAsync())
            .Should().ContainSingle("the re-upload was dropped, not inserted beside the tombstone").Subject;
        row.Id.Should().Be(tombstoneId);
        row.DeletedAt.Should().Be(DeletedOn, "the tombstone row is left untouched");
        value(row).Should().Be(deletedValue, "the re-upload must not be written into the tombstone");

        (await verify.Set<TEntity>().AsNoTracking().ToListAsync())
            .Should().BeEmpty("the record the user deleted stays deleted");
    }

    [Fact]
    public async Task BulkCreate_WhenASystemSweptTombstoneHoldsTheKey_InsertsPastIt()
    {
        var tombstoneId = SeedTombstone(new BolusEntity { Insulin = 5.0 }, deletedByUser: false);
        var broadcaster = new RecordingV4RecordBroadcaster<Bolus>();

        var result = await NewBolusRepository(broadcaster).BulkCreateAsync(
            [ReuploadedBolus(9.0)],
            WriteOrigin.Live);

        result.Should().HaveCount(1);
        broadcaster.Created.Should().HaveCount(1);
        broadcaster.Updated.Should().BeEmpty("nothing was upserted in place");
        await AssertInsertedPastTombstoneAsync<BolusEntity>(
            tombstoneId, e => e.Insulin, deletedValue: 5.0, reuploaded: 9.0);
    }

    [Fact]
    public async Task BulkCreate_WhenASystemSweptGlucoseTombstoneHoldsTheKey_InsertsPastIt()
    {
        var tombstoneId = SeedTombstone(new SensorGlucoseEntity { Mgdl = 120 }, deletedByUser: false);
        var broadcaster = new RecordingV4RecordBroadcaster<SensorGlucose>();
        var repo = new SensorGlucoseRepository(
            new TestTenantDbContextFactory(_context),
            new Mock<IDeduplicationService>().Object,
            new Mock<IAuditContext>().Object,
            NullLogger<SensorGlucoseRepository>.Instance,
            broadcaster);

        var result = await repo.BulkCreateAsync(
            [new SensorGlucose { Timestamp = T0, DataSource = DataSource, SyncIdentifier = SyncIdentifier, Mgdl = 180 }],
            WriteOrigin.Live);

        result.Should().HaveCount(1);
        broadcaster.Created.Should().HaveCount(1);
        broadcaster.Updated.Should().BeEmpty("nothing was upserted in place");
        await AssertInsertedPastTombstoneAsync<SensorGlucoseEntity>(
            tombstoneId, e => e.Mgdl, deletedValue: 120, reuploaded: 180);
    }

    [Fact]
    public async Task BulkCreate_WhenAUserDeletedTombstoneHoldsTheKey_DropsTheReupload()
    {
        var tombstoneId = SeedTombstone(new BolusEntity { Insulin = 5.0 }, deletedByUser: true);
        var broadcaster = new RecordingV4RecordBroadcaster<Bolus>();

        var result = await NewBolusRepository(broadcaster).BulkCreateAsync(
            [ReuploadedBolus(9.0)],
            WriteOrigin.Live);

        result.Should().BeEmpty();
        broadcaster.Created.Should().BeEmpty();
        broadcaster.Updated.Should().BeEmpty();
        await AssertTombstoneStillHoldsTheKeyAsync<BolusEntity>(
            tombstoneId, e => e.Insulin, deletedValue: 5.0);
    }

    /// <remarks>
    /// The connectors that mint a sync identifier per record reuse it as the legacy id, so both
    /// guards see this one; pinned so they never disagree.
    /// </remarks>
    [Fact]
    public async Task BulkCreate_WhenAUserDeletedTombstoneHoldsTheKeyRepeatedAsLegacyId_DropsTheReupload()
    {
        var tombstoneId = SeedTombstone(
            new BolusEntity { Insulin = 5.0, LegacyId = SyncIdentifier }, deletedByUser: true);
        var broadcaster = new RecordingV4RecordBroadcaster<Bolus>();

        var result = await NewBolusRepository(broadcaster).BulkCreateAsync(
            [new Bolus
            {
                Timestamp = T0,
                DataSource = DataSource,
                SyncIdentifier = SyncIdentifier,
                LegacyId = SyncIdentifier,
                Insulin = 9.0,
            }],
            WriteOrigin.Live);

        result.Should().BeEmpty();
        broadcaster.Created.Should().BeEmpty();
        broadcaster.Updated.Should().BeEmpty();
        await AssertTombstoneStillHoldsTheKeyAsync<BolusEntity>(
            tombstoneId, e => e.Insulin, deletedValue: 5.0);
    }

    [Fact]
    public async Task BulkCreate_WhenALiveRowAndAUserTombstoneShareTheKey_UpdatesTheLiveRow()
    {
        var tombstoneId = SeedTombstone(new BolusEntity { Insulin = 5.0 }, deletedByUser: true);
        var liveId = SeedLiveRow(new BolusEntity { Insulin = 7.0 });
        var broadcaster = new RecordingV4RecordBroadcaster<Bolus>();

        var result = await NewBolusRepository(broadcaster).BulkCreateAsync(
            [ReuploadedBolus(9.0)],
            WriteOrigin.Live);

        result.Should().ContainSingle().Which.Id.Should().Be(liveId);
        broadcaster.Created.Should().BeEmpty();
        broadcaster.Updated.Should().ContainSingle(b => b.Id == liveId);
        await AssertLiveRowTookTheReuploadAsync(liveId, tombstoneId);
    }

    private async Task AssertLiveRowTookTheReuploadAsync(Guid liveId, Guid tombstoneId)
    {
        await using var verify = NewContext();
        var rows = await verify.Boluses.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        rows.Single(b => b.Id == liveId).Insulin.Should().Be(9.0);
        rows.Single(b => b.Id == tombstoneId).Insulin.Should().Be(5.0, "the tombstone row is left untouched");
    }

    /// <remarks>
    /// One batch spanning both halves of the rule for the device snapshots, whose bulk upsert is the
    /// wire path an uploader retries on: <see cref="SyncIdentifier"/> is held by a user tombstone and
    /// <see cref="LiveSyncIdentifier"/> by a live row.
    /// </remarks>
    private async Task AssertSnapshotUpsertFollowsTombstonePolicyAsync<TModel, TEntity>(
        TEntity deleted,
        Func<string, double, TModel> build,
        Func<TModel, Task<TModel>> createAsync,
        Func<TModel[], Task<IEnumerable<TModel>>> bulkCreateAsync,
        RecordingV4RecordBroadcaster<TModel> broadcaster,
        Func<TEntity, double?> value)
        where TModel : V4RecordBase
        where TEntity : class, IV4TimeSeriesEntity, ISyncDedupable
    {
        var tombstoneId = SeedTombstone(deleted, deletedByUser: true);
        var live = await createAsync(build(LiveSyncIdentifier, 7.0));

        var result = await bulkCreateAsync([build(SyncIdentifier, 9.0), build(LiveSyncIdentifier, 8.0)]);

        result.Should().ContainSingle().Which.Id.Should().Be(live.Id);
        broadcaster.Created.Should().ContainSingle("the batch inserted nothing").Which.Id.Should().Be(live.Id);
        broadcaster.Updated.Should().ContainSingle().Which.Id.Should().Be(live.Id);

        await using var verify = NewContext();
        var rows = await verify.Set<TEntity>().IgnoreQueryFilters().AsNoTracking().ToListAsync();
        rows.Should().HaveCount(2, "the re-upload neither lands past the tombstone nor duplicates the live row");
        value(rows.Single(e => e.Id == tombstoneId)).Should().Be(5.0, "the tombstone row is left untouched");
        value(rows.Single(e => e.Id == live.Id)).Should().Be(8.0);
    }

    [Fact]
    public async Task BulkCreate_WhenAUserDeletedApsTombstoneHoldsTheKey_DropsItAndUpdatesTheLiveRow()
    {
        var broadcaster = new RecordingV4RecordBroadcaster<ApsSnapshot>();
        var repository = NewApsSnapshotRepository(broadcaster);

        await AssertSnapshotUpsertFollowsTombstonePolicyAsync(
            new ApsSnapshotEntity { AidAlgorithm = nameof(AidAlgorithm.Trio), Iob = 5.0 },
            (sync, iob) => new ApsSnapshot
            {
                Timestamp = T0,
                DataSource = DataSource,
                SyncIdentifier = sync,
                AidAlgorithm = AidAlgorithm.Trio,
                Iob = iob,
            },
            model => repository.CreateAsync(model, WriteOrigin.Live),
            models => repository.BulkCreateAsync(models, WriteOrigin.Live),
            broadcaster,
            e => e.Iob);
    }

    [Fact]
    public async Task BulkCreate_WhenAUserDeletedPumpTombstoneHoldsTheKey_DropsItAndUpdatesTheLiveRow()
    {
        var broadcaster = new RecordingV4RecordBroadcaster<PumpSnapshot>();
        var repository = new PumpSnapshotRepository(
            new TestTenantDbContextFactory(_context),
            new Mock<IAuditContext>().Object,
            NullLogger<PumpSnapshotRepository>.Instance,
            broadcaster);

        await AssertSnapshotUpsertFollowsTombstonePolicyAsync(
            new PumpSnapshotEntity { Reservoir = 5.0 },
            (sync, reservoir) => new PumpSnapshot
            {
                Timestamp = T0,
                DataSource = DataSource,
                SyncIdentifier = sync,
                Reservoir = reservoir,
            },
            model => repository.CreateAsync(model, WriteOrigin.Live),
            models => repository.BulkCreateAsync(models, WriteOrigin.Live),
            broadcaster,
            e => e.Reservoir);
    }

    [Fact]
    public async Task BulkCreate_WhenAUserDeletedUploaderTombstoneHoldsTheKey_DropsItAndUpdatesTheLiveRow()
    {
        var broadcaster = new RecordingV4RecordBroadcaster<UploaderSnapshot>();
        var repository = new UploaderSnapshotRepository(
            new TestTenantDbContextFactory(_context),
            new Mock<IAuditContext>().Object,
            NullLogger<UploaderSnapshotRepository>.Instance,
            broadcaster);

        await AssertSnapshotUpsertFollowsTombstonePolicyAsync(
            new UploaderSnapshotEntity { BatteryVoltage = 5.0 },
            (sync, voltage) => new UploaderSnapshot
            {
                Timestamp = T0,
                DataSource = DataSource,
                SyncIdentifier = sync,
                BatteryVoltage = voltage,
            },
            model => repository.CreateAsync(model, WriteOrigin.Live),
            models => repository.BulkCreateAsync(models, WriteOrigin.Live),
            broadcaster,
            e => e.BatteryVoltage);
    }

    /// <remarks>
    /// One snapshot type pins the system-swept half: the branch is the base's and carries no per-type
    /// code, and the mapper each type contributes is exercised by the user-tombstone tests above. APS
    /// snapshots are the highest-volume of the three — one per loop cycle from every AID uploader.
    /// </remarks>
    [Fact]
    public async Task BulkCreate_WhenASystemSweptApsTombstoneHoldsTheKey_InsertsPastIt()
    {
        var tombstoneId = SeedTombstone(
            new ApsSnapshotEntity { AidAlgorithm = nameof(AidAlgorithm.Trio), Iob = 5.0 },
            deletedByUser: false);
        var broadcaster = new RecordingV4RecordBroadcaster<ApsSnapshot>();

        var result = await NewApsSnapshotRepository(broadcaster).BulkCreateAsync(
            [new ApsSnapshot
            {
                Timestamp = T0,
                DataSource = DataSource,
                SyncIdentifier = SyncIdentifier,
                AidAlgorithm = AidAlgorithm.Trio,
                Iob = 9.0,
            }],
            WriteOrigin.Live);

        result.Should().HaveCount(1);
        broadcaster.Created.Should().HaveCount(1);
        broadcaster.Updated.Should().BeEmpty("nothing was upserted in place");
        await AssertInsertedPastTombstoneAsync<ApsSnapshotEntity>(
            tombstoneId, e => e.Iob!.Value, deletedValue: 5.0, reuploaded: 9.0);
    }

    [Fact]
    public async Task Create_WhenASystemSweptTombstoneHoldsTheKey_InsertsPastIt()
    {
        var tombstoneId = SeedTombstone(new BolusEntity { Insulin = 5.0 }, deletedByUser: false);
        var broadcaster = new RecordingV4RecordBroadcaster<Bolus>();

        var created = await NewBolusRepository(broadcaster).CreateAsync(ReuploadedBolus(9.0), WriteOrigin.Live);

        created.Id.Should().NotBe(tombstoneId);
        broadcaster.Created.Should().HaveCount(1);
        broadcaster.Updated.Should().BeEmpty("nothing was upserted in place");
        await AssertInsertedPastTombstoneAsync<BolusEntity>(
            tombstoneId, e => e.Insulin, deletedValue: 5.0, reuploaded: 9.0);
    }

    [Fact]
    public async Task Create_WhenAUserDeletedTombstoneHoldsTheKey_IsRefused()
    {
        var tombstoneId = SeedTombstone(new BolusEntity { Insulin = 5.0 }, deletedByUser: true);
        var broadcaster = new RecordingV4RecordBroadcaster<Bolus>();

        var act = () => NewBolusRepository(broadcaster).CreateAsync(ReuploadedBolus(9.0), WriteOrigin.Live);

        await act.Should().ThrowAsync<RecreationBlockedException>().WithMessage($"*{SyncIdentifier}*");
        broadcaster.Created.Should().BeEmpty();
        broadcaster.Updated.Should().BeEmpty();
        await AssertTombstoneStillHoldsTheKeyAsync<BolusEntity>(
            tombstoneId, e => e.Insulin, deletedValue: 5.0);
    }

    /// <remarks>
    /// The connectors that mint a sync identifier per record reuse it as the legacy id, so the
    /// single path must see both guards too; pinned so they never disagree.
    /// </remarks>
    [Fact]
    public async Task Create_WhenAUserDeletedTombstoneHoldsTheKeyRepeatedAsLegacyId_IsRefused()
    {
        var tombstoneId = SeedTombstone(
            new BolusEntity { Insulin = 5.0, LegacyId = SyncIdentifier }, deletedByUser: true);
        var broadcaster = new RecordingV4RecordBroadcaster<Bolus>();
        var model = ReuploadedBolus(9.0);
        model.LegacyId = SyncIdentifier;

        var act = () => NewBolusRepository(broadcaster).CreateAsync(model, WriteOrigin.Live);

        await act.Should().ThrowAsync<RecreationBlockedException>();
        broadcaster.Created.Should().BeEmpty();
        await AssertTombstoneStillHoldsTheKeyAsync<BolusEntity>(
            tombstoneId, e => e.Insulin, deletedValue: 5.0);
    }

    [Fact]
    public async Task Create_WhenALiveRowAndAUserTombstoneShareTheKey_UpdatesTheLiveRow()
    {
        var tombstoneId = SeedTombstone(new BolusEntity { Insulin = 5.0 }, deletedByUser: true);
        var liveId = SeedLiveRow(new BolusEntity { Insulin = 7.0 });
        var broadcaster = new RecordingV4RecordBroadcaster<Bolus>();

        var upserted = await NewBolusRepository(broadcaster).CreateAsync(ReuploadedBolus(9.0), WriteOrigin.Live);

        upserted.Id.Should().Be(liveId);
        broadcaster.Created.Should().BeEmpty();
        broadcaster.Updated.Should().ContainSingle(b => b.Id == liveId);
        await AssertLiveRowTookTheReuploadAsync(liveId, tombstoneId);
    }

    private static MeterGlucose ReuploadedMeterGlucose(int mgdl) => new()
    {
        Timestamp = T0,
        DataSource = DataSource,
        LegacyId = LegacyId,
        Mgdl = mgdl,
    };

    [Fact]
    public async Task Create_WhenASystemSweptTombstoneHoldsTheLegacyId_InsertsPastIt()
    {
        var tombstoneId = SeedLegacyIdRow(
            new MeterGlucoseEntity { Mgdl = 120 }, DeletedOn, deletedByUser: false);
        var broadcaster = new RecordingV4RecordBroadcaster<MeterGlucose>();

        var created = await NewMeterGlucoseRepository(broadcaster)
            .CreateAsync(ReuploadedMeterGlucose(180), WriteOrigin.Live);

        created.Id.Should().NotBe(tombstoneId);
        broadcaster.Created.Should().HaveCount(1);
        await AssertInsertedPastTombstoneAsync<MeterGlucoseEntity>(
            tombstoneId, e => e.Mgdl, deletedValue: 120, reuploaded: 180);
    }

    [Fact]
    public async Task Create_WhenAUserDeletedTombstoneHoldsTheLegacyId_IsRefused()
    {
        var tombstoneId = SeedLegacyIdRow(
            new MeterGlucoseEntity { Mgdl = 120 }, DeletedOn, deletedByUser: true);
        var broadcaster = new RecordingV4RecordBroadcaster<MeterGlucose>();

        var act = () => NewMeterGlucoseRepository(broadcaster)
            .CreateAsync(ReuploadedMeterGlucose(180), WriteOrigin.Live);

        await act.Should().ThrowAsync<RecreationBlockedException>().WithMessage($"*{LegacyId}*");
        broadcaster.Created.Should().BeEmpty();
        await AssertTombstoneStillHoldsTheKeyAsync<MeterGlucoseEntity>(
            tombstoneId, e => e.Mgdl, deletedValue: 120);
    }

    /// <remarks>
    /// Device status extras are keyed by <c>CorrelationId</c> where the V4 tables are keyed by
    /// LegacyId, and the repository is off both bases, so its single create carries its own copy of
    /// the guard its <c>BulkCreateAsync</c> applies.
    /// </remarks>
    [Fact]
    public async Task CreateExtras_WhenAUserDeletedTombstoneHoldsTheCorrelationId_IsRefused()
    {
        var correlationId = Guid.CreateVersion7();
        var tombstoneId = SeedExtrasRow(correlationId, DeletedOn, deletedByUser: true);

        var act = () => NewExtrasRepository()
            .CreateAsync(new DeviceStatusExtras { CorrelationId = correlationId, Timestamp = T0 }, WriteOrigin.Live);

        await act.Should().ThrowAsync<RecreationBlockedException>().WithMessage($"*{correlationId}*");
        await using var verify = NewContext();
        var rows = await verify.DeviceStatusExtras.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        rows.Should().ContainSingle("the re-upload was dropped, not inserted beside the tombstone")
            .Which.Id.Should().Be(tombstoneId);
    }

    [Fact]
    public async Task CreateExtras_WhenASystemSweptTombstoneHoldsTheCorrelationId_InsertsPastIt()
    {
        var correlationId = Guid.CreateVersion7();
        var tombstoneId = SeedExtrasRow(correlationId, DeletedOn, deletedByUser: false);

        var created = await NewExtrasRepository()
            .CreateAsync(new DeviceStatusExtras { CorrelationId = correlationId, Timestamp = T0 }, WriteOrigin.Live);

        created.Id.Should().NotBe(tombstoneId);
        await using var verify = NewContext();
        (await verify.DeviceStatusExtras.AsNoTracking().SingleAsync()).Id.Should().Be(created.Id);
    }

    private DeviceStatusExtrasRepository NewExtrasRepository() =>
        new(new TestTenantDbContextFactory(_context), new Mock<IAuditContext>().Object);

    private Guid SeedExtrasRow(Guid correlationId, DateTime? deletedAt, bool deletedByUser)
    {
        var entity = new DeviceStatusExtrasEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = Tenant,
            CorrelationId = correlationId,
            Timestamp = T0,
            DeletedAt = deletedAt,
        };

        using var ctx = NewContext();
        var entry = ctx.DeviceStatusExtras.Add(entity);
        entry.Property("DeletedByUser").CurrentValue = deletedByUser;
        ctx.SaveChanges();
        return entity.Id;
    }

    [Fact]
    public async Task Create_WhenALiveRowHoldsTheLegacyId_IsRefused()
    {
        var liveId = SeedLegacyIdRow(
            new MeterGlucoseEntity { Mgdl = 120 }, deletedAt: null, deletedByUser: false);
        var broadcaster = new RecordingV4RecordBroadcaster<MeterGlucose>();

        var act = () => NewMeterGlucoseRepository(broadcaster)
            .CreateAsync(ReuploadedMeterGlucose(180), WriteOrigin.Live);

        await act.Should().ThrowAsync<RecreationBlockedException>();
        broadcaster.Created.Should().BeEmpty();
        await using var verify = NewContext();
        var row = (await verify.MeterGlucose.AsNoTracking().ToListAsync())
            .Should().ContainSingle("the legacy id already had a row, so nothing was inserted").Subject;
        row.Id.Should().Be(liveId);
        row.Mgdl.Should().Be(120);
    }
}
