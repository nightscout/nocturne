using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

[Trait("Category", "Unit")]
public class CatalogFoodLookupTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private const string ObjectId = "507f1f77bcf86cd799439011";

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly FoodRepository _foods;
    private readonly Guid _appleId = Guid.CreateVersion7();

    public CatalogFoodLookupTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(
            TenantId, "test", new MutationAuditInterceptor());
        _context = _db.CreateContext();
        _foods = new FoodRepository(_context.AsFactory(), NullLogger<FoodRepository>.Instance);
        _context.Foods.AddRange(
            new FoodEntity { Id = _appleId, TenantId = TenantId, Name = "apple", Carbs = 12.5 },
            new FoodEntity { Id = Guid.CreateVersion7(), TenantId = TenantId, OriginalId = ObjectId, Name = "bread", Carbs = 20 });
        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Reference_ThatParsesAsAUuid_FindsTheFoodKeyedByIt()
    {
        var food = await _foods.GetCatalogFoodAsync(_appleId.ToString());

        food.Should().NotBeNull();
        food!.Id.Should().Be(_appleId);
        food.CarbsPerPortion.Should().Be(12.5);
    }

    [Fact]
    public async Task UuidOverload_FindsTheFoodKeyedByIt()
    {
        var food = await _foods.GetCatalogFoodAsync(_appleId);

        food!.CarbsPerPortion.Should().Be(12.5);
    }

    [Fact]
    public async Task Reference_ThatIsNotAUuid_FindsTheFoodByItsObjectId()
    {
        var food = await _foods.GetCatalogFoodAsync(ObjectId);

        food.Should().NotBeNull();
        food!.CarbsPerPortion.Should().Be(20);
    }

    [Fact]
    public async Task UnknownReference_ReturnsNull()
    {
        (await _foods.GetCatalogFoodAsync("ffffffffffffffffffffffff")).Should().BeNull();
        (await _foods.GetCatalogFoodAsync(Guid.NewGuid())).Should().BeNull();
        (await _foods.GetCatalogFoodAsync(Guid.NewGuid().ToString())).Should().BeNull();
    }

    [Fact]
    public async Task Reference_ThatIsTheCoercedV3IdOfAFood_IsNotMatched()
    {
        var served = MongoObjectId.Coerce(_appleId.ToString())!;

        (await _foods.GetCatalogFoodAsync(served)).Should().BeNull();
    }
}
