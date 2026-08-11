using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Infrastructure.Data.Tests.Repositories.V4;

[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
public class UploaderSnapshotRepositoryBulkUpsertTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly NocturneDbContext _context;
    private readonly UploaderSnapshotRepository _repository;

    public UploaderSnapshotRepositoryBulkUpsertTests()
    {
        var dbName = $"uploader_snapshot_upsert_tests_{Guid.NewGuid()}";
        _context = TestDbContextFactory.CreateInMemoryContext(dbName);
        _context.TenantId = TenantA;
        _repository = new UploaderSnapshotRepository(new TestTenantDbContextFactory(_context), new SystemAuditContext(), NullLogger<UploaderSnapshotRepository>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    private static UploaderSnapshot CreateSnapshot(string? syncIdentifier = null, int? battery = null)
    {
        return new UploaderSnapshot
        {
            Timestamp = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            UtcOffset = 0,
            DataSource = "trio",
            SyncIdentifier = syncIdentifier,
            Battery = battery,
        };
    }

    [Fact]
    public async Task BulkUpsertAsync_UpdatesExistingRecordInPlace()
    {
        await _repository.BulkUpsertAsync([CreateSnapshot("sync-1", battery: 90)], WriteOrigin.Live);

        var result = (await _repository.BulkUpsertAsync([CreateSnapshot("sync-1", battery: 85)], WriteOrigin.Live)).ToList();

        result.Should().HaveCount(1);
        _context.UploaderSnapshots.Count().Should().Be(1);
        _context.UploaderSnapshots.Single().Battery.Should().Be(85);
    }

    [Fact]
    public async Task BulkUpsertAsync_InsertsWhenNoKeyMatch()
    {
        await _repository.BulkUpsertAsync([CreateSnapshot("sync-1")], WriteOrigin.Live);

        var result = (await _repository.BulkUpsertAsync([CreateSnapshot("sync-2")], WriteOrigin.Live)).ToList();

        result.Should().HaveCount(1);
        _context.UploaderSnapshots.Count().Should().Be(2);
    }
}
