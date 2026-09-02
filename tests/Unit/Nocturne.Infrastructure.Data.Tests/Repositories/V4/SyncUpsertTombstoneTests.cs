using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Events;
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

    private Guid SeedTombstone<TEntity>(TEntity entity)
        where TEntity : class, IV4TimeSeriesEntity, ISyncDedupable
    {
        entity.Id = Guid.CreateVersion7();
        entity.TenantId = Tenant;
        entity.Timestamp = T0;
        entity.DataSource = DataSource;
        entity.SyncIdentifier = SyncIdentifier;
        entity.DeletedAt = DeletedOn;

        using var ctx = NewContext();
        ctx.Set<TEntity>().Add(entity);
        ctx.SaveChanges();
        return entity.Id;
    }

    private async Task AssertInsertedPastTombstoneAsync<TEntity>(
        Guid tombstoneId, Func<TEntity, double> value, double deletedValue, double reuploaded)
        where TEntity : class, IV4TimeSeriesEntity
    {
        await using var verify = NewContext();

        var tombstone = await verify.Set<TEntity>().IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(e => e.Id == tombstoneId);
        tombstone.DeletedAt.Should().Be(DeletedOn, "the delete the member made still stands");
        value(tombstone).Should().Be(deletedValue, "the re-upload must not be written into the tombstone");

        var live = await verify.Set<TEntity>().AsNoTracking().SingleAsync();
        live.Id.Should().NotBe(tombstoneId);
        value(live).Should().Be(reuploaded, "a filtered read returns the re-uploaded record");
    }

    [Fact]
    public async Task BulkCreate_WhenTheKeyIsHeldByASoftDeletedRow_InsertsPastTheTombstone()
    {
        var tombstoneId = SeedTombstone(new BolusEntity { Insulin = 5.0 });
        var broadcaster = new RecordingV4RecordBroadcaster<Bolus>();
        var repo = new BolusRepository(
            new TestTenantDbContextFactory(_context),
            new Mock<IDeduplicationService>().Object,
            new Mock<IAuditContext>().Object,
            NullLogger<BolusRepository>.Instance,
            broadcaster);

        var result = await repo.BulkCreateAsync(
            [new Bolus { Timestamp = T0, DataSource = DataSource, SyncIdentifier = SyncIdentifier, Insulin = 9.0 }],
            WriteOrigin.Live);

        result.Should().HaveCount(1);
        broadcaster.Created.Should().HaveCount(1);
        broadcaster.Updated.Should().BeEmpty("nothing was upserted in place");
        await AssertInsertedPastTombstoneAsync<BolusEntity>(
            tombstoneId, e => e.Insulin, deletedValue: 5.0, reuploaded: 9.0);
    }

    [Fact]
    public async Task BulkCreate_WhenAGlucoseKeyIsHeldByASoftDeletedRow_InsertsPastTheTombstone()
    {
        var tombstoneId = SeedTombstone(new SensorGlucoseEntity { Mgdl = 120 });
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

}
