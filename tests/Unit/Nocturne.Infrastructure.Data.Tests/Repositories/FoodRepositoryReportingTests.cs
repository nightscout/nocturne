using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// A legacy treatment is served with the name of the food it was eaten as, so every write to a food
/// has to tell <see cref="ILegacyReadCacheInvalidation"/>, once the write has committed.
/// </summary>
[Trait("Category", "Unit")]
public class FoodRepositoryReportingTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly Mock<ILegacyReadCacheInvalidation> _legacyReads = new();
    private readonly List<Type> _written = [];
    private readonly FoodRepository _foods;
    private readonly FailingSaveInterceptor _failingSave = new();

    public FoodRepositoryReportingTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(
            TenantId, "test", new MutationAuditInterceptor(), _failingSave);
        _context = _db.CreateContext();
        _legacyReads
            .Setup(r => r.WrittenAsync(It.IsAny<IReadOnlyCollection<Type>>()))
            .Callback((IReadOnlyCollection<Type> models) => _written.AddRange(models))
            .Returns(Task.CompletedTask);
        _foods = new FoodRepository(_context.AsFactory(), NullLogger<FoodRepository>.Instance, _legacyReads.Object);
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<Guid> SeedAsync(string name)
    {
        var id = Guid.CreateVersion7();
        _context.Foods.Add(new FoodEntity { Id = id, TenantId = TenantId, Name = name, Carbs = 10 });
        await _context.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task ARename_ReportsFoods()
    {
        var id = await SeedAsync("apple");

        var renamed = await _foods.UpdateFoodAsync(id.ToString(), new Food { Name = "pear", Carbs = 10 });

        renamed!.Name.Should().Be("pear");
        _written.Should().Equal(typeof(Food));
    }

    [Fact]
    public async Task AWriteWhoseSaveThrows_ReportsNothing()
    {
        var id = await SeedAsync("apple");
        _failingSave.Armed = true;

        var rename = () => _foods.UpdateFoodAsync(id.ToString(), new Food { Name = "pear", Carbs = 10 });

        await rename.Should().ThrowAsync<InvalidOperationException>();
        _written.Should().BeEmpty();
    }

    [Fact]
    public async Task AnUpdateOfAFoodThatIsNotThere_ReportsNothing()
    {
        await _foods.UpdateFoodAsync(Guid.CreateVersion7().ToString(), new Food { Name = "pear" });

        _written.Should().BeEmpty();
    }

    [Fact]
    public async Task ACreate_ReportsFoods()
    {
        await _foods.CreateFoodAsync([new Food { Name = "apple", Carbs = 10 }]);

        _written.Should().Equal(typeof(Food));
    }

    [Fact]
    public async Task ADelete_ReportsFoods()
    {
        var id = await SeedAsync("apple");

        (await _foods.DeleteFoodAsync(id.ToString())).Should().BeTrue();

        _written.Should().Equal(typeof(Food));
    }

    [Fact]
    public async Task ABulkDelete_ReportsFoodsOnlyWhenItDeletedOne()
    {
        await SeedAsync("apple");

        (await _foods.BulkDeleteFoodAsync("find[name]=nothing")).Should().Be(0);
        _written.Should().BeEmpty();

        (await _foods.BulkDeleteFoodAsync("find[name]=apple")).Should().Be(1);
        _written.Should().Equal(typeof(Food));
    }

    private sealed class FailingSaveInterceptor : SaveChangesInterceptor
    {
        public bool Armed { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            Armed ? throw new InvalidOperationException("save refused") : base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}
