using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Infrastructure.Data.Tests.Repositories.V4;

[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
public class MeterGlucoseRepositoryBulkCreateTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly NocturneDbContext _context;
    private readonly MeterGlucoseRepository _repository;
    private readonly Mock<ILogger<MeterGlucoseRepository>> _logger = new();

    public MeterGlucoseRepositoryBulkCreateTests()
    {
        var dbName = $"meter_glucose_bulk_tests_{Guid.NewGuid()}";
        _context = TestDbContextFactory.CreateInMemoryContext(dbName);
        _context.TenantId = TenantA;
        _logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        _repository = new MeterGlucoseRepository(new TestTenantDbContextFactory(_context), new SystemAuditContext(), _logger.Object);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    private static MeterGlucose CreateRecord(string? legacyId = null, DateTime? timestamp = null)
    {
        return new MeterGlucose
        {
            Timestamp = timestamp ?? new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            UtcOffset = 0,
            LegacyId = legacyId,
            Mgdl = 120,
        };
    }

    [Fact]
    public async Task BulkCreateAsync_InsertsNewRecords()
    {
        var records = new[]
        {
            CreateRecord("legacy-1", new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc)),
            CreateRecord("legacy-2", new DateTime(2026, 5, 1, 11, 0, 0, DateTimeKind.Utc)),
            CreateRecord("legacy-3", new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc)),
        };

        var result = (await _repository.BulkCreateAsync(records, WriteOrigin.Live)).ToList();

        result.Should().HaveCount(3);
        var dbCount = _context.MeterGlucose.Count();
        dbCount.Should().Be(3);
    }

    [Fact]
    public async Task BulkCreateAsync_DeduplicatesByLegacyId_SkipsExisting()
    {
        // Pre-insert a record with legacy-1
        await _repository.CreateAsync(CreateRecord("legacy-1", new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc)), WriteOrigin.Live);

        var records = new[]
        {
            CreateRecord("legacy-1", new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc)),
            CreateRecord("legacy-new", new DateTime(2026, 5, 1, 11, 0, 0, DateTimeKind.Utc)),
        };

        var result = (await _repository.BulkCreateAsync(records, WriteOrigin.Live)).ToList();

        result.Should().HaveCount(1);
        result[0].LegacyId.Should().Be("legacy-new");
        var dbCount = _context.MeterGlucose.Count();
        dbCount.Should().Be(2); // 1 pre-existing + 1 new
    }

    [Fact]
    public async Task BulkCreateAsync_DeduplicatesWithinBatch()
    {
        var records = new[]
        {
            CreateRecord("legacy-dup", new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc)),
            CreateRecord("legacy-dup", new DateTime(2026, 5, 1, 11, 0, 0, DateTimeKind.Utc)),
        };

        var result = (await _repository.BulkCreateAsync(records, WriteOrigin.Live)).ToList();

        result.Should().HaveCount(1);
        var dbCount = _context.MeterGlucose.Count();
        dbCount.Should().Be(1);
    }

    /// <summary>
    /// A user who deletes a stretch of readings and then re-imports it gets nothing back for that
    /// stretch. The write has to say so, and has to tell that apart from a reading it already holds.
    /// </summary>
    [Fact]
    public async Task BulkCreateAsync_WhenTheUserDeletedSomeRecords_WritesTheRestAndCountsTheDeleted()
    {
        var at = new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc);
        await _repository.BulkCreateAsync(
            [CreateRecord("legacy-deleted", at), CreateRecord("legacy-live", at)], WriteOrigin.Live);
        var deleted = _context.MeterGlucose.Single(e => e.LegacyId == "legacy-deleted");
        deleted.DeletedAt = DateTime.UtcNow;
        _context.Entry(deleted).Property("DeletedByUser").CurrentValue = true;
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        _logger.Invocations.Clear();

        var written = await _repository.BulkCreateAsync(
            [CreateRecord("legacy-deleted", at), CreateRecord("legacy-live", at), CreateRecord("legacy-new", at)],
            WriteOrigin.Live);

        written.Select(r => r.LegacyId).Should().BeEquivalentTo(["legacy-new"]);
        written.SkippedDeleted.Should().Be(1, "the live duplicate is already stored, not lost");
        _context.MeterGlucose.Count().Should().Be(2);
        VerifyWarnings(Times.Once());
    }

    /// <summary>
    /// A batch repeating one identity asks for one record, so a deletion holding it skips one record,
    /// not one per repeat.
    /// </summary>
    [Fact]
    public async Task BulkCreateAsync_CountsARepeatedDeletedIdentityOnce()
    {
        var at = new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc);
        await _repository.BulkCreateAsync([CreateRecord("legacy-deleted", at)], WriteOrigin.Live);
        var deleted = _context.MeterGlucose.Single(e => e.LegacyId == "legacy-deleted");
        deleted.DeletedAt = DateTime.UtcNow;
        _context.Entry(deleted).Property("DeletedByUser").CurrentValue = true;
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var written = await _repository.BulkCreateAsync(
            [CreateRecord("legacy-deleted", at), CreateRecord("legacy-deleted", at.AddMinutes(5))],
            WriteOrigin.Live);

        written.Should().BeEmpty();
        written.SkippedDeleted.Should().Be(1);
    }

    [Fact]
    public async Task BulkCreateAsync_WhenNothingWasDeleted_ReportsNoSkips()
    {
        var at = new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc);
        await _repository.BulkCreateAsync([CreateRecord("legacy-1", at)], WriteOrigin.Live);

        var written = await _repository.BulkCreateAsync(
            [CreateRecord("legacy-1", at), CreateRecord("legacy-2", at)], WriteOrigin.Live);

        written.SkippedDeleted.Should().Be(0);
        VerifyWarnings(Times.Never());
    }

    private void VerifyWarnings(Times times) =>
        _logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            times);

    [Fact]
    public async Task BulkCreateAsync_EmptyInput_ReturnsEmpty()
    {
        var result = (await _repository.BulkCreateAsync([], WriteOrigin.Live)).ToList();

        result.Should().BeEmpty();
        var dbCount = _context.MeterGlucose.Count();
        dbCount.Should().Be(0);
    }
}
