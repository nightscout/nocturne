using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.V4;
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

    private Guid SeedTombstone<TEntity>(TEntity entity, bool deletedByUser)
        where TEntity : class, IV4TimeSeriesEntity, ISyncDedupable
    {
        entity.Id = Guid.CreateVersion7();
        entity.TenantId = Tenant;
        entity.Timestamp = T0;
        entity.DataSource = DataSource;
        entity.SyncIdentifier = SyncIdentifier;
        entity.DeletedAt = DeletedOn;

        using var ctx = NewContext();
        var entry = ctx.Set<TEntity>().Add(entity);
        entry.Property("DeletedByUser").CurrentValue = deletedByUser;
        ctx.SaveChanges();
        return entity.Id;
    }

    private BolusRepository NewBolusRepository(RecordingV4RecordBroadcaster<Bolus> broadcaster) =>
        new(new TestTenantDbContextFactory(_context),
            new Mock<IDeduplicationService>().Object,
            new Mock<IAuditContext>().Object,
            NullLogger<BolusRepository>.Instance,
            broadcaster);

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
            [new Bolus { Timestamp = T0, DataSource = DataSource, SyncIdentifier = SyncIdentifier, Insulin = 9.0 }],
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
            [new Bolus { Timestamp = T0, DataSource = DataSource, SyncIdentifier = SyncIdentifier, Insulin = 9.0 }],
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
        var broadcaster = new RecordingV4RecordBroadcaster<Bolus>();
        var repository = NewBolusRepository(broadcaster);
        var live = await repository.CreateAsync(
            new Bolus { Timestamp = T0, DataSource = DataSource, SyncIdentifier = SyncIdentifier, Insulin = 7.0 },
            WriteOrigin.Live);

        var result = await repository.BulkCreateAsync(
            [new Bolus { Timestamp = T0, DataSource = DataSource, SyncIdentifier = SyncIdentifier, Insulin = 9.0 }],
            WriteOrigin.Live);

        result.Should().ContainSingle().Which.Id.Should().Be(live.Id);
        broadcaster.Created.Should().ContainSingle(b => b.Id == live.Id, "the single create broadcast once");
        broadcaster.Updated.Should().ContainSingle(b => b.Id == live.Id);
        await using var verify = NewContext();
        var rows = await verify.Boluses.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        rows.Single(b => b.Id == live.Id).Insulin.Should().Be(9.0);
        rows.Single(b => b.Id == tombstoneId).Insulin.Should().Be(5.0, "the tombstone row is left untouched");
    }
}
