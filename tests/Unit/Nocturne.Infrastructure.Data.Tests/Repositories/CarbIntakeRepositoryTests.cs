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
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
[Trait("Category", "CarbIntake")]
public class CarbIntakeRepositoryTests : IDisposable
{
    private static readonly Guid TestTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private readonly DbConnection _connection;
    private readonly DbContextOptions<NocturneDbContext> _contextOptions;
    private readonly NocturneDbContext _context;
    private readonly Mock<IDeduplicationService> _mockDeduplicationService;
    private readonly CarbIntakeRepository _repo;

    public CarbIntakeRepositoryTests()
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

        _repo = new CarbIntakeRepository(
            new TestTenantDbContextFactory(_context),
            _mockDeduplicationService.Object,
            new Mock<IAuditContext>().Object,
            NullLogger<CarbIntakeRepository>.Instance);
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
        var timestamp = DateTime.UtcNow;
        var first = await _repo.CreateAsync(new CarbIntake
        {
            Timestamp = timestamp,
            DataSource = "aaps",
            SyncIdentifier = "sync-1",
            Carbs = 30.0,
        }, WriteOrigin.Live);

        var second = await _repo.CreateAsync(new CarbIntake
        {
            Timestamp = timestamp,
            DataSource = "aaps",
            SyncIdentifier = "sync-1",
            Carbs = 42.0,
        }, WriteOrigin.Live);

        second.Id.Should().Be(first.Id);
        second.Carbs.Should().Be(42.0);
        var count = await _context.CarbIntakes.CountAsync();
        count.Should().Be(1);
    }

    [Fact]
    public async Task CreateAsync_WithoutSyncIdentifier_DoesNotDedupe()
    {
        var timestamp = DateTime.UtcNow;
        await _repo.CreateAsync(new CarbIntake { Timestamp = timestamp, Carbs = 30.0 }, WriteOrigin.Live);
        await _repo.CreateAsync(new CarbIntake { Timestamp = timestamp, Carbs = 30.0 }, WriteOrigin.Live);

        var count = await _context.CarbIntakes.CountAsync();
        count.Should().Be(2);
    }

    [Fact]
    public async Task CreateAsync_WithoutDataSource_DoesNotDedupe()
    {
        var timestamp = DateTime.UtcNow;
        await _repo.CreateAsync(new CarbIntake { Timestamp = timestamp, SyncIdentifier = "sync-1", Carbs = 30.0 }, WriteOrigin.Live);
        await _repo.CreateAsync(new CarbIntake { Timestamp = timestamp, SyncIdentifier = "sync-1", Carbs = 30.0 }, WriteOrigin.Live);

        var count = await _context.CarbIntakes.CountAsync();
        count.Should().Be(2);
    }

    [Fact]
    public async Task CreateAsync_WithSameSyncIdentifierDifferentDataSource_InsertsBoth()
    {
        var timestamp = DateTime.UtcNow;
        await _repo.CreateAsync(new CarbIntake
        {
            Timestamp = timestamp,
            DataSource = "aaps",
            SyncIdentifier = "sync-1",
            Carbs = 30.0,
        }, WriteOrigin.Live);
        await _repo.CreateAsync(new CarbIntake
        {
            Timestamp = timestamp,
            DataSource = "loop",
            SyncIdentifier = "sync-1",
            Carbs = 30.0,
        }, WriteOrigin.Live);

        var count = await _context.CarbIntakes.CountAsync();
        count.Should().Be(2);
    }

    [Fact]
    public async Task BulkCreateAsync_WithDuplicateSyncIdentifierInBatch_DeduplicatesByUpsert()
    {
        var timestamp = DateTime.UtcNow;
        var existing = await _repo.CreateAsync(new CarbIntake
        {
            Timestamp = timestamp,
            DataSource = "aaps",
            SyncIdentifier = "sync-1",
            Carbs = 30.0,
        }, WriteOrigin.Live);

        var results = (await _repo.BulkCreateAsync(new[]
        {
            new CarbIntake { Timestamp = timestamp, DataSource = "aaps", SyncIdentifier = "sync-1", Carbs = 42.0 },
            new CarbIntake { Timestamp = timestamp, DataSource = "aaps", SyncIdentifier = "sync-2", Carbs = 15.0 },
        }, WriteOrigin.Live)).ToList();

        results.Should().HaveCount(2);
        var dbCount = await _context.CarbIntakes.CountAsync();
        dbCount.Should().Be(2);

        var updated = await _context.CarbIntakes.FindAsync(existing.Id);
        updated!.Carbs.Should().Be(42.0);

        // The returned enumerable contains the updated row with the new payload
        results.Should().ContainSingle(r => r.Id == existing.Id && r.Carbs == 42.0);
        // And the new insert
        results.Should().ContainSingle(r => r.SyncIdentifier == "sync-2" && r.Carbs == 15.0);

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
    public async Task CountAsync_ExcludesNonPrimaryDuplicates()
    {
        var timestamp = DateTime.UtcNow;

        // The same meal imported by two connectors (e.g. MyLife pump data that
        // also flows through Glooko) — two distinct rows.
        var primary = await _repo.CreateAsync(new CarbIntake
        {
            Timestamp = timestamp,
            DataSource = "mylife-connector",
            LegacyId = "mylife-1",
            Carbs = 50.0,
        }, WriteOrigin.Live);
        var duplicate = await _repo.CreateAsync(new CarbIntake
        {
            Timestamp = timestamp,
            DataSource = "glooko-connector",
            LegacyId = "glooko-1",
            Carbs = 50.0,
        }, WriteOrigin.Live);

        // Dedup links them into one canonical group; the Glooko row is non-primary.
        var canonicalId = Guid.CreateVersion7();
        var mills = new DateTimeOffset(timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds();
        _context.LinkedRecords.AddRange(
            new LinkedRecordEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = TestTenantId,
                CanonicalId = canonicalId,
                RecordType = "carbintake",
                RecordId = primary.Id,
                SourceTimestamp = mills,
                DataSource = "mylife-connector",
                IsPrimary = true,
            },
            new LinkedRecordEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = TestTenantId,
                CanonicalId = canonicalId,
                RecordType = "carbintake",
                RecordId = duplicate.Id,
                SourceTimestamp = mills,
                DataSource = "glooko-connector",
                IsPrimary = false,
            });
        await _context.SaveChangesAsync();

        // GetAsync already drops the non-primary duplicate; CountAsync must agree
        // so pagination totals match the returned rows.
        var fetched = (await _repo.GetAsync(
            from: null, to: null, device: null, source: null)).ToList();
        var count = await _repo.CountAsync(from: null, to: null);

        fetched.Should().HaveCount(1);
        count.Should().Be(1);
    }

    [Fact]
    public async Task BulkCreateAsync_WithIntraBatchSyncIdentifierCollision_DeduplicatesToLatest()
    {
        var timestamp = DateTime.UtcNow;
        var results = await _repo.BulkCreateAsync(new[]
        {
            new CarbIntake { Timestamp = timestamp, DataSource = "aaps", SyncIdentifier = "sync-1", Carbs = 30.0 },
            new CarbIntake { Timestamp = timestamp, DataSource = "aaps", SyncIdentifier = "sync-1", Carbs = 42.0 },
        }, WriteOrigin.Live);

        var dbCount = await _context.CarbIntakes.CountAsync();
        dbCount.Should().Be(1);
        var only = await _context.CarbIntakes.FirstAsync();
        only.Carbs.Should().Be(42.0);
    }
}
