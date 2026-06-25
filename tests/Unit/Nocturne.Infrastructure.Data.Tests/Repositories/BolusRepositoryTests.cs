using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
[Trait("Category", "Bolus")]
public class BolusRepositoryTests : IDisposable
{
    private static readonly Guid TestTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private readonly DbConnection _connection;
    private readonly DbContextOptions<NocturneDbContext> _contextOptions;
    private readonly NocturneDbContext _context;
    private readonly Mock<IDeduplicationService> _mockDeduplicationService;
    private readonly BolusRepository _repo;

    public BolusRepositoryTests()
    {
        // Create in-memory SQLite database for testing — mirrors the pattern in
        // TreatmentRepositoryTests so partial unique indexes (e.g. on
        // (tenant_id, data_source, sync_identifier)) are enforced end-to-end.
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        _contextOptions = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .EnableSensitiveDataLogging()
            .Options;

        // Create the database schema and seed the tenant.
        using (var seedContext = new NocturneDbContext(_contextOptions))
        {
            seedContext.TenantId = TestTenantId;
            seedContext.Database.EnsureCreated();
            seedContext.Tenants.Add(new TenantEntity { Id = TestTenantId, Slug = "test" });
            seedContext.SaveChanges();
        }

        _context = new NocturneDbContext(_contextOptions);
        _context.TenantId = TestTenantId;

        _mockDeduplicationService = new Mock<IDeduplicationService>();
        _mockDeduplicationService
            .Setup(d => d.GetOrCreateCanonicalIdAsync(
                It.IsAny<RecordType>(),
                It.IsAny<long>(),
                It.IsAny<MatchCriteria>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());
        _mockDeduplicationService
            .Setup(d => d.LinkRecordAsync(
                It.IsAny<Guid>(),
                It.IsAny<RecordType>(),
                It.IsAny<Guid>(),
                It.IsAny<long>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _repo = new BolusRepository(
            new TestTenantDbContextFactory(_context),
            _mockDeduplicationService.Object,
            new Mock<IAuditContext>().Object,
            NullLogger<BolusRepository>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task CreateAsync_WithExistingSyncIdentifier_UpdatesInPlace()
    {
        // Arrange: seed a Bolus with DataSource="aaps", SyncIdentifier="sync-1", Insulin=5.0
        var timestamp = DateTime.UtcNow;
        var first = await _repo.CreateAsync(new Bolus
        {
            Timestamp = timestamp,
            DataSource = "aaps",
            SyncIdentifier = "sync-1",
            Insulin = 5.0,
        });

        // Act: Create again with same (DataSource, SyncIdentifier), different Insulin
        var second = await _repo.CreateAsync(new Bolus
        {
            Timestamp = timestamp,
            DataSource = "aaps",
            SyncIdentifier = "sync-1",
            Insulin = 6.4,  // updated delivered value
        });

        // Assert: same Id, new payload, only one row exists
        second.Id.Should().Be(first.Id);
        second.Insulin.Should().Be(6.4);
        var count = await _context.Boluses.CountAsync();
        count.Should().Be(1);
    }

    [Fact]
    public async Task CreateAsync_WithoutSyncIdentifier_DoesNotDedupe()
    {
        var timestamp = DateTime.UtcNow;
        await _repo.CreateAsync(new Bolus { Timestamp = timestamp, Insulin = 5.0 });
        await _repo.CreateAsync(new Bolus { Timestamp = timestamp, Insulin = 5.0 });

        var count = await _context.Boluses.CountAsync();
        count.Should().Be(2);
    }

    [Fact]
    public async Task GetLatestTimestampAsync_IsScopedToSource()
    {
        // Regression: the per-source watermark must ignore other sources. connector-a's latest is
        // older than connector-b's; a tenant-global latest would resume connector-a from b's clock
        // and silently skip connector-a's backfill.
        var aOld = new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc);
        var aLatest = new DateTime(2026, 1, 12, 0, 0, 0, DateTimeKind.Utc);
        var bNewer = new DateTime(2026, 1, 20, 0, 0, 0, DateTimeKind.Utc);

        await _repo.CreateAsync(new Bolus { Timestamp = aOld, Insulin = 1.0, DataSource = "connector-a" });
        await _repo.CreateAsync(new Bolus { Timestamp = aLatest, Insulin = 2.0, DataSource = "connector-a" });
        await _repo.CreateAsync(new Bolus { Timestamp = bNewer, Insulin = 3.0, DataSource = "connector-b" });

        (await _repo.GetLatestTimestampAsync("connector-a")).Should().Be(aLatest);
        (await _repo.GetLatestTimestampAsync()).Should().Be(bNewer, "a null source returns the tenant-wide latest");
    }

    [Fact]
    public async Task GetLatestTimestampAsync_ReturnsNull_WhenNoRecordsForSource()
    {
        await _repo.CreateAsync(new Bolus { Timestamp = DateTime.UtcNow, Insulin = 1.0, DataSource = "connector-b" });

        (await _repo.GetLatestTimestampAsync("connector-a")).Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_WithoutDataSource_DoesNotDedupe()
    {
        // SyncIdentifier alone is not enough — needs DataSource scoping.
        var timestamp = DateTime.UtcNow;
        await _repo.CreateAsync(new Bolus { Timestamp = timestamp, SyncIdentifier = "sync-1", Insulin = 5.0 });
        await _repo.CreateAsync(new Bolus { Timestamp = timestamp, SyncIdentifier = "sync-1", Insulin = 5.0 });

        var count = await _context.Boluses.CountAsync();
        count.Should().Be(2);
    }

    [Fact]
    public async Task CreateAsync_WithSameSyncIdentifierDifferentDataSource_InsertsBoth()
    {
        var timestamp = DateTime.UtcNow;
        await _repo.CreateAsync(new Bolus
        {
            Timestamp = timestamp,
            DataSource = "aaps",
            SyncIdentifier = "sync-1",
            Insulin = 5.0,
        });
        await _repo.CreateAsync(new Bolus
        {
            Timestamp = timestamp,
            DataSource = "loop",
            SyncIdentifier = "sync-1",
            Insulin = 5.0,
        });

        var count = await _context.Boluses.CountAsync();
        count.Should().Be(2);
    }

    [Fact]
    public async Task BulkCreateAsync_WithDuplicateSyncIdentifierInBatch_DeduplicatesByUpsert()
    {
        var timestamp = DateTime.UtcNow;
        // Seed an existing record
        var existing = await _repo.CreateAsync(new Bolus
        {
            Timestamp = timestamp,
            DataSource = "aaps",
            SyncIdentifier = "sync-1",
            Insulin = 5.0,
        });

        // Bulk insert with one colliding SyncIdentifier + one new
        var results = (await _repo.BulkCreateAsync(new[]
        {
            new Bolus { Timestamp = timestamp, DataSource = "aaps", SyncIdentifier = "sync-1", Insulin = 6.4 },
            new Bolus { Timestamp = timestamp, DataSource = "aaps", SyncIdentifier = "sync-2", Insulin = 3.0 },
        })).ToList();

        results.Should().HaveCount(2);
        var dbCount = await _context.Boluses.CountAsync();
        dbCount.Should().Be(2);  // existing updated + one new = 2 rows

        // Original row was updated in place
        var updated = await _context.Boluses.FindAsync(existing.Id);
        updated!.Insulin.Should().Be(6.4);

        // The returned enumerable contains the updated row with the new payload
        results.Should().ContainSingle(r => r.Id == existing.Id && r.Insulin == 6.4);
        // And the new insert
        results.Should().ContainSingle(r => r.SyncIdentifier == "sync-2" && r.Insulin == 3.0);

        // LinkRecordAsync was NOT called for the updated-in-place row
        _mockDeduplicationService.Verify(
            d => d.LinkRecordAsync(
                It.IsAny<Guid>(),
                It.IsAny<RecordType>(),
                existing.Id,
                It.IsAny<long>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task BulkCreateAsync_WithIntraBatchSyncIdentifierCollision_DeduplicatesToLatest()
    {
        // Two records in the same batch with the same (DataSource, SyncIdentifier) — last wins.
        var timestamp = DateTime.UtcNow;
        var results = await _repo.BulkCreateAsync(new[]
        {
            new Bolus { Timestamp = timestamp, DataSource = "aaps", SyncIdentifier = "sync-1", Insulin = 5.0 },
            new Bolus { Timestamp = timestamp, DataSource = "aaps", SyncIdentifier = "sync-1", Insulin = 6.4 },
        });

        var dbCount = await _context.Boluses.CountAsync();
        dbCount.Should().Be(1);
        var only = await _context.Boluses.FirstAsync();
        only.Insulin.Should().Be(6.4);
    }
}
