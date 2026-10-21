using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

[Trait("Category", "Unit")]
public class FoodFindTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly FoodRepository _foods;

    public FoodFindTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(
            TenantId, "test", new MutationAuditInterceptor());
        _context = _db.CreateContext();
        _foods = new FoodRepository(_context.AsFactory(), NullLogger<FoodRepository>.Instance);

        _context.Foods.AddRange(
            Food("apple", "fruit", "synthetic"),
            Food("pear", "fruit", "other"),
            Food("synthetic bread", "bakery", "other"));
        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private static FoodEntity Food(string name, string category, string subcategory) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = TenantId,
            Name = name,
            Category = category,
            Subcategory = subcategory,
        };

    [Fact]
    public async Task Count_ByBracketedCategory_CountsExactlyThatCategory()
    {
        (await _foods.CountFoodAsync("find[category]=fruit")).Should().Be(2);
    }

    [Fact]
    public async Task Count_ByBracketedField_DoesNotMatchTheValueInAnotherField()
    {
        (await _foods.CountFoodAsync("find[category]=synthetic")).Should().Be(0);
        (await _foods.CountFoodAsync("find[subcategory]=synthetic")).Should().Be(1);
    }

    [Fact]
    public async Task Count_ByOperator_AppliesTheOperator()
    {
        (await _foods.CountFoodAsync("find[name][$ne]=apple")).Should().Be(2);
    }

    [Fact]
    public async Task Count_WithBareText_IsNotASubstringSearch()
    {
        (await _foods.CountFoodAsync("synthetic")).Should().Be(3);
    }

    [Fact]
    public async Task Count_WithAFieldTheCollectionLacks_IsRefused()
    {
        var act = () => _foods.CountFoodAsync("find[bogus]=1");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Read_ByBracketedCategory_ReturnsExactlyThatCategoryPaged()
    {
        var all = await _foods.GetFoodWithAdvancedFilterAsync(10, 0, "find[category]=fruit", type: null);
        var page = await _foods.GetFoodWithAdvancedFilterAsync(1, 1, "find[category]=fruit", type: null);

        all.Select(f => f.Name).Should().Equal("apple", "pear");
        page.Select(f => f.Name).Should().Equal("pear");
    }

    [Fact]
    public async Task Read_WithAType_AppliesTheFindWithinIt()
    {
        var foods = await _foods.GetFoodWithAdvancedFilterAsync(10, 0, "find[category]=fruit", "food");

        foods.Select(f => f.Name).Should().Equal("apple", "pear");
        (await _foods.CountFoodAsync("find[category]=fruit", "quickpick")).Should().Be(0);
    }

    [Fact]
    public async Task Read_WithAFieldTheCollectionLacks_IsRefused()
    {
        var act = () => _foods.GetFoodWithAdvancedFilterAsync(10, 0, "find[bogus]=1", type: null);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Search_MatchesBySubstringOfName()
    {
        var foods = await _foods.SearchFoodAsync("read", 10, 0);

        foods.Select(f => f.Name).Should().Equal("synthetic bread");
    }

    [Fact]
    public async Task Search_MatchesCategoryAndSubcategory()
    {
        (await _foods.SearchFoodAsync("fruit", 10, 0)).Should().HaveCount(2);
        (await _foods.SearchFoodAsync("synthetic", 10, 0)).Should().HaveCount(2);
    }
}
