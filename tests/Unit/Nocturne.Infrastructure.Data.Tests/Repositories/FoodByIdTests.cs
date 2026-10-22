using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// A v3 food is served under <see cref="MongoObjectId.Coerce"/> of its id, so a read by that
/// identifier must find the food a UUID keys.
/// </summary>
[Trait("Category", "Unit")]
public class FoodByIdTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly FoodRepository _foods;

    public FoodByIdTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(
            TenantId, "test", new MutationAuditInterceptor());
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
    public async Task FoodKeyedByUuid_IsFoundByTheObjectIdItIsServedUnder()
    {
        var id = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        _context.Foods.AddRange(
            new FoodEntity { Id = id, TenantId = TenantId, Name = "apple", Carbs = 10 },
            new FoodEntity { Id = other, TenantId = TenantId, Name = "bread", Carbs = 20 });
        await _context.SaveChangesAsync();

        var food = await _foods.GetFoodByIdAsync(MongoObjectId.Coerce(id.ToString())!);

        food.Should().NotBeNull();
        food!.Name.Should().Be("apple");
    }

    [Fact]
    public async Task FoodKeyedByUuid_IsUpdatedAndDeletedByTheObjectIdItIsServedUnder()
    {
        var id = Guid.CreateVersion7();
        _context.Foods.AddRange(
            new FoodEntity { Id = id, TenantId = TenantId, Name = "apple", Carbs = 10 },
            new FoodEntity { Id = Guid.CreateVersion7(), TenantId = TenantId, Name = "bread", Carbs = 20 });
        await _context.SaveChangesAsync();
        var served = MongoObjectId.Coerce(id.ToString())!;

        var updated = await _foods.UpdateFoodAsync(served, new Food { Name = "apple", Carbs = 25 });
        var deleted = await _foods.DeleteFoodAsync(served);

        updated!.Id.Should().Be(id.ToString());
        updated.Carbs.Should().Be(25);
        deleted.Should().BeTrue();
        (await _foods.GetFoodAsync()).Select(f => f.Name).Should().Equal("bread");
    }

    [Fact]
    public async Task FoodWithAnUppercaseLegacyId_IsFoundUpdatedAndDeletedByTheHashItIsServedUnder()
    {
        const string legacyId = "5F1D7A3B9C0E4A2B8D6C1E0F";
        _context.Foods.AddRange(
            new FoodEntity { Id = Guid.CreateVersion7(), OriginalId = legacyId, TenantId = TenantId, Name = "apple", Carbs = 10 },
            new FoodEntity { Id = Guid.CreateVersion7(), OriginalId = "5F1D7A3B9C0E4A2B8D6C1E0E", TenantId = TenantId, Name = "bread", Carbs = 20 });
        await _context.SaveChangesAsync();
        var served = MongoObjectId.Coerce(legacyId)!;

        (await _foods.GetFoodByIdAsync(served))!.Name.Should().Be("apple");
        (await _foods.UpdateFoodAsync(served, new Food { Name = "apple", Carbs = 25 }))!.Carbs.Should().Be(25);
        (await _foods.DeleteFoodAsync(served)).Should().BeTrue();
        (await _foods.GetFoodAsync()).Select(f => f.Name).Should().Equal("bread");
    }

    [Fact]
    public async Task UnknownObjectId_FindsNothing()
    {
        (await _foods.GetFoodByIdAsync("5f1d7a3b9c0e4a2b8d6c1e0f")).Should().BeNull();
    }
}
