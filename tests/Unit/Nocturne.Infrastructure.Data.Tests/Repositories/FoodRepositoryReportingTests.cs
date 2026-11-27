using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Core.Contracts.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Nocturne.Tests.Shared.Mocks;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// Every food write publishes the foods it created, updated or deleted once it has committed: the legacy
/// <c>food</c> socket announces them, and a legacy treatment is served with the name of the food it was
/// eaten as.
/// </summary>
[Trait("Category", "Unit")]
public class FoodRepositoryReportingTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly RecordingDocumentChanges<Food> _changes = new();
    private readonly FoodRepository _foods;
    private readonly FailingSaveInterceptor _failingSave = new();

    public FoodRepositoryReportingTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(
            TenantId, "test", new MutationAuditInterceptor(), _failingSave);
        _context = _db.CreateContext();
        _foods = new FoodRepository(_context.AsFactory(), NullLogger<FoodRepository>.Instance, _changes);
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
    public async Task ARename_PublishesTheFoodAsUpdated()
    {
        var id = await SeedAsync("apple");

        await _foods.UpdateFoodAsync(id.ToString(), new Food { Name = "pear", Carbs = 10 });

        _changes.Updated.Should().ContainSingle().Which.Name.Should().Be("pear");
        _changes.Changes.Should().ContainSingle();
    }

    [Fact]
    public async Task AWriteWhoseSaveThrows_PublishesNothing()
    {
        var id = await SeedAsync("apple");
        _failingSave.Armed = true;

        var rename = () => _foods.UpdateFoodAsync(id.ToString(), new Food { Name = "pear", Carbs = 10 });

        await rename.Should().ThrowAsync<InvalidOperationException>();
        _changes.Batches.Should().BeEmpty();
    }

    [Fact]
    public async Task AnUpdateOfAFoodThatIsNotThere_PublishesNothing()
    {
        await _foods.UpdateFoodAsync(Guid.CreateVersion7().ToString(), new Food { Name = "pear" });

        _changes.Batches.Should().BeEmpty();
    }

    [Fact]
    public async Task ACreate_PublishesTheFoodAsCreated()
    {
        await _foods.CreateFoodAsync([new Food { Name = "apple", Carbs = 10 }], WriteOrigin.Live);

        _changes.Created.Should().ContainSingle().Which.Name.Should().Be("apple");
        _changes.Updated.Should().BeEmpty();
    }

    [Fact]
    public async Task ACreateThatReplacesALiveFood_PublishesItAsUpdated()
    {
        var id = await SeedAsync("apple");

        await _foods.CreateFoodAsync([new Food { Id = id.ToString(), Name = "pear", Carbs = 10 }], WriteOrigin.Live);

        _changes.Updated.Should().ContainSingle().Which.Name.Should().Be("pear");
        _changes.Created.Should().BeEmpty();
    }

    [Fact]
    public async Task ABackfill_IsPublishedWithItsOrigin()
    {
        await _foods.CreateFoodAsync([new Food { Name = "apple", Carbs = 10 }], WriteOrigin.Backfill);

        var change = _changes.Changes.Should().ContainSingle().Subject;
        change.Origin.Should().Be(WriteOrigin.Backfill);
        change.Created.Should().ContainSingle();
    }

    [Fact]
    public async Task ADelete_PublishesTheDeletedFood()
    {
        var id = await SeedAsync("apple");

        (await _foods.DeleteFoodAsync(id.ToString(), WriteOrigin.Live)).Should().BeTrue();

        _changes.Deleted.Should().ContainSingle().Which.Name.Should().Be("apple");
    }

    [Fact]
    public async Task ABulkDelete_PublishesTheDeletedFoods_OnlyWhenItDeletedOne()
    {
        await SeedAsync("apple");

        (await _foods.BulkDeleteFoodAsync("find[name]=nothing")).Should().Be(0);
        _changes.Batches.Should().BeEmpty();

        (await _foods.BulkDeleteFoodAsync("find[name]=apple")).Should().Be(1);
        _changes.Deleted.Should().ContainSingle().Which.Name.Should().Be("apple");
    }

    [Fact]
    public async Task ABulkDeleteOverTheCap_PublishesOnlyThatFoodsWereWritten()
    {
        var over = AuditedBulkDeleteExtensions.BroadcastMaterializationCap + 1;
        _context.Foods.AddRange(Enumerable.Range(0, over).Select(_ =>
            new FoodEntity { Id = Guid.CreateVersion7(), TenantId = TenantId, Name = "apple", Carbs = 10 }));
        await _context.SaveChangesAsync();

        (await _foods.BulkDeleteFoodAsync("find[name]=apple")).Should().Be(over);

        var change = _changes.Changes.Should().ContainSingle().Subject;
        change.RecordType.Should().Be(typeof(Food));
        change.HasRecords.Should().BeFalse();
    }

    private sealed class FailingSaveInterceptor : SaveChangesInterceptor
    {
        public bool Armed { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            Armed ? throw new InvalidOperationException("save refused") : base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}
