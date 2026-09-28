using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// The v3 food <c>history/{lastModified}</c> read pages on the server write stamp, as Nightscout's
/// <c>lib/api3/generic/history</c> pages on <c>srvModified</c>: a food created before the client's
/// cursor and edited after it must be delivered again.
/// </summary>
[Trait("Category", "Unit")]
public class FoodHistoryTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateTime Written = new(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly FoodRepository _foods;

    public FoodHistoryTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(TenantId);
        _context = _db.CreateContext();
        _foods = new FoodRepository(_context, NullLogger<FoodRepository>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task FoodEditedAfterTheCursor_IsDeliveredAgain_AndUntouchedFoodsAreNot()
    {
        var apple = await SeedAsync("apple", Written);
        await SeedAsync("bread", Written.AddMilliseconds(1));

        var initial = await _foods.GetFoodModifiedSinceAsync(0, 1000);
        initial.Records.Select(f => f.Name).Should().Equal("apple", "bread");
        var cursor = initial.CursorMills!.Value;

        var edited = (await _foods.GetFoodByIdAsync(apple.ToString()))!;
        edited.Carbs = 14;
        await _foods.UpdateFoodAsync(apple.ToString(), edited);

        var next = await _foods.GetFoodModifiedSinceAsync(cursor, 1000);

        next.Records.Should().ContainSingle().Which.Carbs.Should().Be(14);
        next.CursorMills.Should().BeGreaterThan(cursor);
        (await _foods.GetFoodModifiedSinceAsync(next.CursorMills!.Value, 1000)).Records.Should().BeEmpty();
    }

    [Fact]
    public async Task Paging_AdvancesThroughTheBacklogInWriteOrder()
    {
        await SeedAsync("first", Written);
        await SeedAsync("second", Written.AddMilliseconds(1));
        await SeedAsync("third", Written.AddMilliseconds(2));

        var page = await _foods.GetFoodModifiedSinceAsync(0, 2);
        page.Records.Select(f => f.Name).Should().Equal("first", "second");

        var rest = await _foods.GetFoodModifiedSinceAsync(page.CursorMills!.Value, 2);
        rest.Records.Select(f => f.Name).Should().Equal("third");
    }

    /// <summary>
    /// Inserts a food, then sets its write stamp alone — a touch the save path keeps as assigned.
    /// </summary>
    private async Task<Guid> SeedAsync(string name, DateTime written)
    {
        var id = Guid.CreateVersion7();
        _context.Foods.Add(new FoodEntity { Id = id, TenantId = TenantId, Name = name, Carbs = 10 });
        await _context.SaveChangesAsync();

        var entity = await _context.Foods.SingleAsync(f => f.Id == id);
        entity.SysUpdatedAt = written;
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return id;
    }
}
