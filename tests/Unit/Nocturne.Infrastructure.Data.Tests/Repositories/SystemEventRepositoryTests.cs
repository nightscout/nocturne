using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
public class SystemEventRepositoryTests : IDisposable
{
    private static readonly Guid TestTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly SystemEventRepository _repository;
    private readonly SaveCounter _saves = new();

    public SystemEventRepositoryTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(TestTenantId, "test", _saves);
        _context = _db.CreateContext();
        _repository = new SystemEventRepository(_context);
    }

    private sealed class SaveCounter : SaveChangesInterceptor
    {
        public int Count { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Count++;
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private static SystemEvent Event(string originalId, string code, long mills) => new()
    {
        EventType = SystemEventType.Warning,
        Category = SystemEventCategory.Pump,
        Code = code,
        Description = $"event {code}",
        Mills = mills,
        Source = "connector",
        OriginalId = originalId,
    };

    private Task<List<SystemEventEntity>> RowsForAsync(string originalId) =>
        _context.SystemEvents.AsNoTracking().Where(e => e.OriginalId == originalId).ToListAsync();

    [Fact]
    public async Task BulkUpsertAsync_MixedBatch_UpdatesStoredRowsWithoutDuplicating()
    {
        await _repository.UpsertSystemEventAsync(Event("evt-stored", "old", 1_000));
        var storedId = (await RowsForAsync("evt-stored")).Single().Id;

        var count = await _repository.BulkUpsertAsync(
        [
            Event("evt-stored", "new", 1_500),
            Event("evt-a", "first", 2_000),
            Event("evt-a", "second", 2_500),
            Event("evt-b", "only", 3_000),
        ]);

        count.Should().Be(4);
        var stored = (await RowsForAsync("evt-stored")).Should().ContainSingle().Subject;
        stored.Id.Should().Be(storedId);
        stored.Code.Should().Be("new");
        stored.Mills.Should().Be(1_500);
        (await RowsForAsync("evt-a")).Should().ContainSingle()
            .Which.Code.Should().Be("second", "a repeated OriginalId updates the row its first occurrence inserted");
        (await RowsForAsync("evt-b")).Should().ContainSingle();
    }

    [Fact]
    public async Task BulkUpsertAsync_SavesOncePerBatch()
    {
        await _repository.UpsertSystemEventAsync(Event("evt-stored", "old", 1_000));
        var savesBefore = _saves.Count;

        await _repository.BulkUpsertAsync(
        [
            Event("evt-stored", "new", 1_500),
            Event("evt-a", "first", 2_000),
            Event("evt-b", "only", 3_000),
        ]);

        _saves.Count.Should().Be(savesBefore + 1);
        (await _context.SystemEvents.AsNoTracking().CountAsync()).Should().Be(3);
    }

    [Fact]
    public async Task BulkUpsertAsync_LeavesNoSystemEventTracked_EvenForDuplicateStoredRows()
    {
        _context.SystemEvents.AddRange(
            SystemEventMapper.ToEntity(Event("evt-dup", "one", 1_000)),
            SystemEventMapper.ToEntity(Event("evt-dup", "two", 1_100)));
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        await _repository.BulkUpsertAsync([Event("evt-dup", "new", 1_500), Event("evt-a", "first", 2_000)]);

        _context.ChangeTracker.Entries<SystemEventEntity>().Should().BeEmpty();
    }

    [Fact]
    public async Task UpsertSystemEventAsync_ReturnsTheStoredEvent()
    {
        await _repository.UpsertSystemEventAsync(Event("evt-stored", "old", 1_000));

        var result = await _repository.UpsertSystemEventAsync(Event("evt-stored", "new", 1_500));

        result.OriginalId.Should().Be("evt-stored");
        result.Code.Should().Be("new");
        result.Mills.Should().Be(1_500);
        (await RowsForAsync("evt-stored")).Should().ContainSingle();
    }
}
