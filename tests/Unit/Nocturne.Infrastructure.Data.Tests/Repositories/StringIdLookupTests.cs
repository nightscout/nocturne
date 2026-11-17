using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Repositories.Health;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// A record written under any string id is read, updated and deleted by that id: an ObjectId by
/// its stored original id, a GUID by itself, and anything else by the key derived from it.
/// </summary>
[Trait("Category", "Unit")]
public class StringIdLookupTests : IDisposable
{
    private const string ObjectId = "5f1d7a3b9c0e4a2b8d6c1e0f";

    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;

    public StringIdLookupTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(
            TenantId, "test", new MutationAuditInterceptor());
        _context = _db.CreateContext();
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private BodyWeightRepository BodyWeights() => new(_context.AsFactory(), TimeProvider.System);

    private FoodRepository Foods() => new(_context.AsFactory(), NullLogger<FoodRepository>.Instance);

    private SettingsRepository Settings() => new(_context.AsFactory());

    public static TheoryData<string> Ids() => new()
    {
        "scale-0001",
        ObjectId,
        "0194f2a0-7b1c-7c3e-9d2a-4e5f6a7b8c9d",
    };

    [Theory]
    [MemberData(nameof(Ids))]
    public async Task ABodyWeight_IsFetchedUpdatedAndDeletedByItsId(string id)
    {
        var repository = BodyWeights();
        await repository.UpsertAsync([new BodyWeight { Id = id, Mills = 1_000, WeightKg = 70 }]);
        await repository.UpsertAsync([new BodyWeight { Id = "other", Mills = 2_000, WeightKg = 90 }]);

        (await repository.GetByIdAsync(id))!.WeightKg.Should().Be(70);
        (await repository.UpdateAsync(id, new BodyWeight { Id = id, Mills = 1_000, WeightKg = 71 }))!
            .WeightKg.Should().Be(71);
        (await repository.DeleteAsync(id)).Should().BeTrue();
        (await repository.GetByIdAsync(id)).Should().BeNull();
        (await repository.GetByIdAsync("other")).Should().NotBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task ABodyWeight_IsNotFoundByAnEmptyId(string? id)
    {
        await BodyWeights().UpsertAsync([new BodyWeight { Id = "scale-0001", Mills = 1_000, WeightKg = 70 }]);

        (await BodyWeights().GetByIdAsync(id!)).Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public async Task AFood_IsFetchedUpdatedAndDeletedByItsId(string id)
    {
        var repository = Foods();
        await repository.CreateFoodAsync([new Food { Id = id, Name = "apple", Carbs = 10 }]);
        await repository.CreateFoodAsync([new Food { Id = "other", Name = "bread", Carbs = 20 }]);

        (await repository.GetFoodByIdAsync(id))!.Name.Should().Be("apple");
        (await repository.UpdateFoodAsync(id, new Food { Name = "apple", Carbs = 25 }))!
            .Carbs.Should().Be(25);
        (await repository.DeleteFoodAsync(id)).Should().BeTrue();
        (await repository.GetFoodByIdAsync(id)).Should().BeNull();
        (await repository.GetFoodByIdAsync("other")).Should().NotBeNull();
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public async Task ASetting_IsFetchedUpdatedAndDeletedByItsId(string id)
    {
        var repository = Settings();
        await repository.CreateSettingsAsync([new Settings { Id = id, Key = "pref-a", Value = "1" }]);
        await repository.CreateSettingsAsync([new Settings { Id = "other", Key = "pref-b", Value = "2" }]);

        (await repository.GetSettingsByIdAsync(id))!.Key.Should().Be("pref-a");
        (await repository.UpdateSettingsAsync(id, new Settings { Key = "pref-a", Value = "3" }))!
            .Value!.ToString().Should().Be("3");
        (await repository.DeleteSettingsAsync(id)).Should().BeTrue();
        (await repository.GetSettingsByIdAsync(id)).Should().BeNull();
        (await repository.GetSettingsByIdAsync("other")).Should().NotBeNull();
    }

    [Fact]
    public async Task AMigratedBodyWeight_IsFetchedUpdatedAndDeletedByItsObjectId()
    {
        _context.BodyWeights.Add(new BodyWeightEntity
        {
            Id = Guid.CreateVersion7(), OriginalId = ObjectId, TenantId = TenantId, Mills = 1_000, WeightKg = 70,
        });
        await _context.SaveChangesAsync();
        var repository = BodyWeights();

        (await repository.GetByIdAsync(ObjectId))!.WeightKg.Should().Be(70);
        (await repository.UpdateAsync(ObjectId, new BodyWeight { Id = ObjectId, Mills = 1_000, WeightKg = 71 }))!
            .WeightKg.Should().Be(71);
        (await repository.DeleteAsync(ObjectId)).Should().BeTrue();
        (await repository.GetByIdAsync(ObjectId)).Should().BeNull();
    }

    [Fact]
    public async Task AMigratedFood_IsFetchedUpdatedAndDeletedByItsObjectId()
    {
        _context.Foods.Add(new FoodEntity
        {
            Id = Guid.CreateVersion7(), OriginalId = ObjectId, TenantId = TenantId, Name = "apple", Carbs = 10,
        });
        await _context.SaveChangesAsync();
        var repository = Foods();

        (await repository.GetFoodByIdAsync(ObjectId))!.Name.Should().Be("apple");
        (await repository.UpdateFoodAsync(ObjectId, new Food { Name = "apple", Carbs = 25 }))!
            .Carbs.Should().Be(25);
        (await repository.DeleteFoodAsync(ObjectId)).Should().BeTrue();
        (await repository.GetFoodByIdAsync(ObjectId)).Should().BeNull();
    }

    [Fact]
    public async Task AMigratedSetting_IsFetchedUpdatedAndDeletedByItsObjectId()
    {
        _context.Settings.Add(new SettingsEntity
        {
            Id = Guid.CreateVersion7(), OriginalId = ObjectId, TenantId = TenantId, Key = "pref-a", Value = "1",
        });
        await _context.SaveChangesAsync();
        var repository = Settings();

        (await repository.GetSettingsByIdAsync(ObjectId))!.Key.Should().Be("pref-a");
        (await repository.UpdateSettingsAsync(ObjectId, new Settings { Key = "pref-a", Value = "3" }))!
            .Value!.ToString().Should().Be("3");
        (await repository.DeleteSettingsAsync(ObjectId)).Should().BeTrue();
        (await repository.GetSettingsByIdAsync(ObjectId)).Should().BeNull();
    }
}
