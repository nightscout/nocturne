using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories.V4;

[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
public class DeviceStatusExtrasRepositoryBulkCreateTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly NocturneDbContext _context;
    private readonly DeviceStatusExtrasRepository _repository;

    public DeviceStatusExtrasRepositoryBulkCreateTests()
    {
        var dbName = $"device_status_extras_bulk_tests_{Guid.NewGuid()}";
        _context = TestDbContextFactory.CreateInMemoryContext(dbName);
        _context.TenantId = TenantA;
        _repository = new DeviceStatusExtrasRepository(_context, NullLogger<DeviceStatusExtrasRepository>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    private static DeviceStatusExtras CreateRecord(Guid? correlationId = null, DateTime? timestamp = null)
    {
        return new DeviceStatusExtras
        {
            Timestamp = timestamp ?? new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            CorrelationId = correlationId ?? Guid.NewGuid(),
        };
    }

    [Fact]
    public async Task BulkCreateAsync_InsertsNewRecords()
    {
        var records = new[]
        {
            CreateRecord(Guid.NewGuid(), new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc)),
            CreateRecord(Guid.NewGuid(), new DateTime(2026, 5, 1, 11, 0, 0, DateTimeKind.Utc)),
            CreateRecord(Guid.NewGuid(), new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc)),
        };

        var result = (await _repository.BulkCreateAsync(records)).ToList();

        result.Should().HaveCount(3);
        var dbCount = _context.DeviceStatusExtras.Count();
        dbCount.Should().Be(3);
    }

    [Fact]
    public async Task BulkCreateAsync_DeduplicatesByCorrelationId_SkipsExisting()
    {
        var existingCorrelationId = Guid.NewGuid();
        var newCorrelationId = Guid.NewGuid();

        // Pre-insert a record
        await _repository.CreateAsync(CreateRecord(existingCorrelationId, new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc)));

        var records = new[]
        {
            CreateRecord(existingCorrelationId, new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc)),
            CreateRecord(newCorrelationId, new DateTime(2026, 5, 1, 11, 0, 0, DateTimeKind.Utc)),
        };

        var result = (await _repository.BulkCreateAsync(records)).ToList();

        result.Should().HaveCount(1);
        result[0].CorrelationId.Should().Be(newCorrelationId);
        var dbCount = _context.DeviceStatusExtras.Count();
        dbCount.Should().Be(2); // 1 pre-existing + 1 new
    }

    [Fact]
    public async Task BulkCreateAsync_DeduplicatesWithinBatch()
    {
        var sharedCorrelationId = Guid.NewGuid();
        var records = new[]
        {
            CreateRecord(sharedCorrelationId, new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc)),
            CreateRecord(sharedCorrelationId, new DateTime(2026, 5, 1, 11, 0, 0, DateTimeKind.Utc)),
        };

        var result = (await _repository.BulkCreateAsync(records)).ToList();

        result.Should().HaveCount(1);
        var dbCount = _context.DeviceStatusExtras.Count();
        dbCount.Should().Be(1);
    }

    [Fact]
    public async Task BulkCreateAsync_EmptyInput_ReturnsEmpty()
    {
        var result = (await _repository.BulkCreateAsync([])).ToList();

        result.Should().BeEmpty();
        var dbCount = _context.DeviceStatusExtras.Count();
        dbCount.Should().Be(0);
    }
}
