using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories.V4;

[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
public class UploaderSnapshotRepositoryBulkCreateTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly NocturneDbContext _context;
    private readonly UploaderSnapshotRepository _repository;

    public UploaderSnapshotRepositoryBulkCreateTests()
    {
        var dbName = $"uploader_snapshot_bulk_tests_{Guid.NewGuid()}";
        _context = TestDbContextFactory.CreateInMemoryContext(dbName);
        _context.TenantId = TenantA;
        _repository = new UploaderSnapshotRepository(_context, NullLogger<UploaderSnapshotRepository>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    private static UploaderSnapshot CreateSnapshot(string? legacyId = null, DateTime? timestamp = null)
    {
        return new UploaderSnapshot
        {
            Timestamp = timestamp ?? new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            UtcOffset = 0,
            LegacyId = legacyId,
        };
    }

    [Fact]
    public async Task BulkCreateAsync_InsertsNewRecords()
    {
        var snapshots = new[]
        {
            CreateSnapshot("legacy-1", new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc)),
            CreateSnapshot("legacy-2", new DateTime(2026, 5, 1, 11, 0, 0, DateTimeKind.Utc)),
            CreateSnapshot("legacy-3", new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc)),
        };

        var result = (await _repository.BulkCreateAsync(snapshots)).ToList();

        result.Should().HaveCount(3);
        var dbCount = _context.UploaderSnapshots.Count();
        dbCount.Should().Be(3);
    }

    [Fact]
    public async Task BulkCreateAsync_DeduplicatesByLegacyId_SkipsExisting()
    {
        // Pre-insert a record with legacy-1
        await _repository.CreateAsync(CreateSnapshot("legacy-1", new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc)));

        var snapshots = new[]
        {
            CreateSnapshot("legacy-1", new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc)),
            CreateSnapshot("legacy-new", new DateTime(2026, 5, 1, 11, 0, 0, DateTimeKind.Utc)),
        };

        var result = (await _repository.BulkCreateAsync(snapshots)).ToList();

        result.Should().HaveCount(1);
        result[0].LegacyId.Should().Be("legacy-new");
        var dbCount = _context.UploaderSnapshots.Count();
        dbCount.Should().Be(2); // 1 pre-existing + 1 new
    }

    [Fact]
    public async Task BulkCreateAsync_DeduplicatesWithinBatch()
    {
        var snapshots = new[]
        {
            CreateSnapshot("legacy-dup", new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc)),
            CreateSnapshot("legacy-dup", new DateTime(2026, 5, 1, 11, 0, 0, DateTimeKind.Utc)),
        };

        var result = (await _repository.BulkCreateAsync(snapshots)).ToList();

        result.Should().HaveCount(1);
        var dbCount = _context.UploaderSnapshots.Count();
        dbCount.Should().Be(1);
    }

    [Fact]
    public async Task BulkCreateAsync_EmptyInput_ReturnsEmpty()
    {
        var result = (await _repository.BulkCreateAsync([])).ToList();

        result.Should().BeEmpty();
        var dbCount = _context.UploaderSnapshots.Count();
        dbCount.Should().Be(0);
    }
}
