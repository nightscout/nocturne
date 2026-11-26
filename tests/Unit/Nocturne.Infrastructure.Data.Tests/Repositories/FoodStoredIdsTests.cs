using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// <see cref="FoodRepository.GetStoredIdsAsync"/> answers each requested id as the food create path
/// stores it, including two spellings of one UUID in one batch.
/// </summary>
[Trait("Category", "Unit")]
public class FoodStoredIdsTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly FoodRepository _foods;

    public FoodStoredIdsTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(TenantId, "test");
        _context = _db.CreateContext();
        _foods = new FoodRepository(_context.AsFactory(), NullLogger<FoodRepository>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task EachSpellingOfAStoredId_IsAnswered_WithWhetherItsFoodIsDeleted()
    {
        var live = Guid.CreateVersion7().ToString();
        const string deleted = "5f8d0c1e8a7b4c3d9e5f0001";
        await _foods.CreateFoodAsync([new Food { Id = live, Name = "apple", Carbs = 10 }]);
        await _foods.StoreDeletedAsync([new Food { Id = deleted, Name = "bread", Carbs = 20 }]);

        var stored = await _foods.GetStoredIdsAsync([live, live.ToUpperInvariant(), deleted, "5f8d0c1e8a7b4c3d9e5f0002"]);

        stored.Should().BeEquivalentTo(new Dictionary<string, bool>
        {
            [live] = false,
            [live.ToUpperInvariant()] = false,
            [deleted] = true,
        });
    }
}
