using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;
using Nocturne.Core.Contracts.V4;
using Nocturne.Tests.Shared.Mocks;

namespace Nocturne.Infrastructure.Data.Tests.Repositories.V4;

[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
public class ApsSnapshotRepositoryBulkUpsertTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly NocturneDbContext _context;
    private readonly ApsSnapshotRepository _repository;

    public ApsSnapshotRepositoryBulkUpsertTests()
    {
        var dbName = $"aps_snapshot_upsert_tests_{Guid.NewGuid()}";
        _context = TestDbContextFactory.CreateInMemoryContext(dbName);
        _context.TenantId = TenantA;
        _repository = new ApsSnapshotRepository(new TestTenantDbContextFactory(_context), new SystemAuditContext(), NullLogger<ApsSnapshotRepository>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    private static ApsSnapshot CreateSnapshot(
        string? syncIdentifier = null,
        string? dataSource = "trio",
        DateTime? timestamp = null,
        double? iob = null)
    {
        return new ApsSnapshot
        {
            Timestamp = timestamp ?? new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            UtcOffset = 0,
            DataSource = dataSource,
            SyncIdentifier = syncIdentifier,
            AidAlgorithm = AidAlgorithm.Trio,
            Enacted = false,
            Iob = iob,
        };
    }

    [Fact]
    public async Task BulkUpsertAsync_InsertsNewRecords()
    {
        var snapshots = new[]
        {
            CreateSnapshot("sync-1", timestamp: new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc)),
            CreateSnapshot("sync-2", timestamp: new DateTime(2026, 5, 1, 11, 0, 0, DateTimeKind.Utc)),
        };

        var result = (await _repository.BulkUpsertAsync(snapshots, WriteOrigin.Live)).ToList();

        result.Should().HaveCount(2);
        _context.ApsSnapshots.Count().Should().Be(2);
    }

    [Fact]
    public async Task BulkUpsertAsync_UpdatesExistingRecordInPlace()
    {
        await _repository.BulkUpsertAsync([CreateSnapshot("sync-1", iob: 1.0)], WriteOrigin.Live);

        var retry = CreateSnapshot("sync-1", iob: 2.5);
        var result = (await _repository.BulkUpsertAsync([retry], WriteOrigin.Live)).ToList();

        result.Should().HaveCount(1);
        _context.ApsSnapshots.Count().Should().Be(1);
        _context.ApsSnapshots.Single().Iob.Should().Be(2.5);
    }

    [Fact]
    public async Task BulkUpsertAsync_KeepsLastOccurrenceWithinBatch()
    {
        var snapshots = new[]
        {
            CreateSnapshot("sync-dup", iob: 1.0),
            CreateSnapshot("sync-dup", iob: 3.0),
        };

        var result = (await _repository.BulkUpsertAsync(snapshots, WriteOrigin.Live)).ToList();

        result.Should().HaveCount(1);
        _context.ApsSnapshots.Count().Should().Be(1);
        _context.ApsSnapshots.Single().Iob.Should().Be(3.0);
    }

    [Fact]
    public async Task BulkUpsertAsync_MixedBatch_UpdatesMatchedAndInsertsRest()
    {
        await _repository.BulkUpsertAsync([CreateSnapshot("sync-existing", iob: 1.0)], WriteOrigin.Live);

        var snapshots = new[]
        {
            CreateSnapshot("sync-existing", iob: 4.0),
            CreateSnapshot("sync-new"),
        };

        var result = (await _repository.BulkUpsertAsync(snapshots, WriteOrigin.Live)).ToList();

        result.Should().HaveCount(2);
        _context.ApsSnapshots.Count().Should().Be(2);
        _context.ApsSnapshots.Single(e => e.SyncIdentifier == "sync-existing").Iob.Should().Be(4.0);
    }

    [Fact]
    public async Task BulkUpsertAsync_RecordsWithoutSyncKeyAlwaysInsert()
    {
        var unkeyed = new[]
        {
            CreateSnapshot(syncIdentifier: null, timestamp: new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc)),
            CreateSnapshot(syncIdentifier: null, timestamp: new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc)),
        };

        await _repository.BulkUpsertAsync(unkeyed, WriteOrigin.Live);
        await _repository.BulkUpsertAsync(unkeyed, WriteOrigin.Live);

        _context.ApsSnapshots.Count().Should().Be(4);
    }

    [Fact]
    public async Task BulkUpsertAsync_SameSyncIdDifferentSource_DoesNotCollide()
    {
        var snapshots = new[]
        {
            CreateSnapshot("sync-1", dataSource: "trio"),
            CreateSnapshot("sync-1", dataSource: "loop"),
        };

        var result = (await _repository.BulkUpsertAsync(snapshots, WriteOrigin.Live)).ToList();

        result.Should().HaveCount(2);
        _context.ApsSnapshots.Count().Should().Be(2);
    }

    [Fact]
    public async Task BulkUpsertAsync_EmptyInput_ReturnsEmpty()
    {
        var result = (await _repository.BulkUpsertAsync([], WriteOrigin.Live)).ToList();

        result.Should().BeEmpty();
        _context.ApsSnapshots.Count().Should().Be(0);
    }

    [Fact]
    public async Task BulkUpsertAsync_ByteIdenticalRetry_DoesNotBroadcastUpdates()
    {
        var broadcaster = new RecordingV4RecordBroadcaster<ApsSnapshot>();
        var repository = new ApsSnapshotRepository(
            new TestTenantDbContextFactory(_context), new SystemAuditContext(), NullLogger<ApsSnapshotRepository>.Instance, broadcaster);

        await repository.BulkUpsertAsync([CreateSnapshot("sync-1", iob: 1.0)], WriteOrigin.Live);
        broadcaster.Created.Should().HaveCount(1);

        // Byte-identical retry: matched in place, but nothing materially changed.
        await repository.BulkUpsertAsync([CreateSnapshot("sync-1", iob: 1.0)], WriteOrigin.Live);
        broadcaster.Updated.Should().BeEmpty();

        // A real change does broadcast as an update.
        await repository.BulkUpsertAsync([CreateSnapshot("sync-1", iob: 2.0)], WriteOrigin.Live);
        broadcaster.Updated.Should().HaveCount(1);
    }
}
