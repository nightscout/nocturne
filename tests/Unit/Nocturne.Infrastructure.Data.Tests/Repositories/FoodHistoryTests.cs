using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Infrastructure.Data.Interceptors;
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
        _db = TestDbContextFactory.CreateSqliteWithTenant(
            TenantId, "test", new MutationAuditInterceptor(Mock.Of<IHttpContextAccessor>()));
        _context = _db.CreateContext();
        _context.AuditContext = new UserAuditContext();
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

    [Fact]
    public async Task DeletedFood_IsDeliveredAgainWithIsValidFalse_AndHiddenFromEveryOtherRead()
    {
        var apple = await SeedAsync("apple", Written);
        var cursor = (await _foods.GetFoodModifiedSinceAsync(0, 1000)).CursorMills!.Value;

        (await _foods.DeleteFoodAsync(apple.ToString())).Should().BeTrue();

        var next = await _foods.GetFoodModifiedSinceAsync(cursor, 1000);
        var tombstone = next.Records.Should().ContainSingle().Subject;
        tombstone.Name.Should().Be("apple");
        tombstone.IsValid.Should().BeFalse();
        tombstone.SrvModified.Should().BeGreaterThan(cursor).And.Be(next.CursorMills);
        next.CursorMills.Should().BeGreaterThan(cursor);

        (await _context.Foods.IgnoreQueryFilters().Select(f => EF.Property<bool>(f, "DeletedByUser")).SingleAsync())
            .Should().BeTrue("a user's delete is attributed, so a connector re-import leaves it deleted");
        (await _context.Set<MutationAuditLogEntity>().Where(l => l.Action == "delete").CountAsync()).Should().Be(1);

        (await _foods.GetFoodByIdAsync(apple.ToString())).Should().BeNull();
        (await _foods.GetFoodAsync()).Should().BeEmpty();
        (await _foods.DeleteFoodAsync(apple.ToString())).Should().BeFalse();
    }

    [Fact]
    public async Task DeletingAFood_ReleasesItsAttributionsAndFavorites_AsTheForeignKeysDidOnAHardDelete()
    {
        var apple = await SeedAsync("apple", Written);
        var carbIntakeId = Guid.CreateVersion7();
        _context.CarbIntakes.Add(new Nocturne.Infrastructure.Data.Entities.V4.CarbIntakeEntity
        {
            Id = carbIntakeId, TenantId = TenantId, Timestamp = Written, Carbs = 20,
        });
        _context.TreatmentFoods.Add(new TreatmentFoodEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, CarbIntakeId = carbIntakeId, FoodId = apple, Carbs = 20,
        });
        _context.ConnectorFoodEntries.Add(new ConnectorFoodEntryEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, ConnectorSource = "test-connector",
            ExternalEntryId = "entry-1", ExternalFoodId = "food-1", FoodId = apple,
        });
        _context.UserFoodFavorites.Add(new UserFoodFavoriteEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, UserId = "user-1", FoodId = apple,
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        await _foods.BulkDeleteFoodAsync("apple");

        (await _context.TreatmentFoods.SingleAsync()).FoodId.Should().BeNull();
        (await _context.ConnectorFoodEntries.SingleAsync()).FoodId.Should().BeNull();
        (await _context.UserFoodFavorites.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task RecreatingADeletedFood_RestoresItsRow()
    {
        var apple = await SeedAsync("apple", Written);
        await _foods.DeleteFoodAsync(apple.ToString());

        var restored = await _foods.CreateFoodAsync([new Food { Id = apple.ToString(), Name = "apple", Carbs = 12 }]);

        restored.Should().ContainSingle().Which.Carbs.Should().Be(12);
        var food = (await _foods.GetFoodByIdAsync(apple.ToString()))!;
        food.Carbs.Should().Be(12);
        food.IsValid.Should().BeNull();
        (await _context.Foods.IgnoreQueryFilters().CountAsync()).Should().Be(1);
        (await _context.Foods.Select(f => EF.Property<bool>(f, "DeletedByUser")).SingleAsync())
            .Should().BeFalse("a restored food is live again, and its next delete is judged afresh");
    }

    private sealed class UserAuditContext : IAuditContext
    {
        public Guid? SubjectId => Guid.Empty;
        public string? SubjectName => "tester";
        public string? AuthType => "SessionCookie";
        public string? IpAddress => "127.0.0.1";
        public Guid? TokenId => null;
        public string? TraceId => null;
        public string? Endpoint => "DELETE /api/v3/food";
        public bool IsSystem => false;
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
