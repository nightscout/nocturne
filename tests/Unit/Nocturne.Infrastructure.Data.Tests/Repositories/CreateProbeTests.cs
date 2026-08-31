using System.Data.Common;
using System.Text;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// The create paths for foods and settings are upserts: they resolve the row the incoming id names
/// before inserting. Both populations that carry a legacy Nightscout <c>_id</c> — rows written by
/// the migration job, and rows written by the mapper before its key derivation changed — hold that
/// id in <c>OriginalId</c> and a primary key that does not follow from it.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
public class CreateProbeTests : IDisposable
{
    private const string LegacyObjectId = "5f8d0d55b54764421b7156c3";

    private static readonly Guid TestTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly DbConnection _connection;
    private readonly DbContextOptions<NocturneDbContext> _options;
    private readonly NocturneDbContext _context;
    private readonly FoodRepository _foods;
    private readonly SettingsRepository _settings;

    public CreateProbeTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .EnableSensitiveDataLogging()
            .Options;

        using (var seed = new NocturneDbContext(_options) { TenantId = TestTenantId })
        {
            seed.Database.EnsureCreated();
            seed.Tenants.Add(new TenantEntity { Id = TestTenantId, Slug = "test" });
            seed.SaveChanges();
        }

        _context = new NocturneDbContext(_options) { TenantId = TestTenantId };
        _foods = new FoodRepository(_context);
        _settings = new SettingsRepository(_context);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>The key the mapper derived before it moved to the shared SHA1 derivation.</summary>
    private static Guid PaddedKey(string id) =>
        new(Encoding.UTF8.GetBytes(id.PadRight(16, '0')[..16]));

    private Guid SeedFood(Guid id, string? originalId, string name)
    {
        _context.Foods.Add(new FoodEntity
        {
            Id = id,
            TenantId = TestTenantId,
            OriginalId = originalId,
            Name = name,
            Carbs = 10,
        });
        _context.SaveChanges();
        _context.ChangeTracker.Clear();
        return id;
    }

    private Guid SeedSetting(Guid id, string? originalId, string key, string value)
    {
        _context.Settings.Add(new SettingsEntity
        {
            Id = id,
            TenantId = TestTenantId,
            OriginalId = originalId,
            Key = key,
            Value = value,
        });
        _context.SaveChanges();
        _context.ChangeTracker.Clear();
        return id;
    }

    private async Task<List<FoodEntity>> ReadFoodsAsync()
    {
        await using var verify = new NocturneDbContext(_options) { TenantId = TestTenantId };
        return await verify.Foods.AsNoTracking().ToListAsync();
    }

    private async Task<List<SettingsEntity>> ReadSettingsAsync()
    {
        await using var verify = new NocturneDbContext(_options) { TenantId = TestTenantId };
        return await verify.Settings.AsNoTracking().ToListAsync();
    }

    [Fact]
    public async Task CreateFood_UpdatesAMigratedRowInPlace_WhenTheLegacyIdComesBack()
    {
        // What MigrationJobService writes: a minted key, the Mongo _id kept as OriginalId.
        var storedId = SeedFood(Guid.CreateVersion7(), LegacyObjectId, "Apple");

        await _foods.CreateFoodAsync([new Food { Id = LegacyObjectId, Name = "Apple (edited)", Carbs = 12 }]);

        var rows = await ReadFoodsAsync();
        rows.Should().ContainSingle();
        rows[0].Id.Should().Be(storedId, "the stored row keeps its own primary key");
        rows[0].OriginalId.Should().Be(LegacyObjectId);
        rows[0].Name.Should().Be("Apple (edited)");
        rows[0].Carbs.Should().Be(12);
    }

    [Fact]
    public async Task CreateFood_UpdatesARowStoredUnderTheOldDerivation_WhenTheLegacyIdComesBack()
    {
        var storedId = SeedFood(PaddedKey(LegacyObjectId), LegacyObjectId, "Apple");

        await _foods.CreateFoodAsync([new Food { Id = LegacyObjectId, Name = "Apple (edited)" }]);

        var rows = await ReadFoodsAsync();
        rows.Should().ContainSingle();
        rows[0].Id.Should().Be(storedId);
        rows[0].Name.Should().Be("Apple (edited)");
    }

    [Fact]
    public async Task CreateFood_KeepsTheLegacyIdAddressable_WhenTheRowIsMatchedByItsKey()
    {
        var storedId = SeedFood(Guid.CreateVersion7(), LegacyObjectId, "Apple");

        await _foods.CreateFoodAsync([new Food { Id = storedId.ToString(), Name = "Apple (edited)" }]);

        var rows = await ReadFoodsAsync();
        rows.Should().ContainSingle();
        rows[0].OriginalId.Should().Be(LegacyObjectId, "an update must not drop the legacy handle reads resolve on");
    }

    [Fact]
    public async Task CreateFood_InsertsWhenNothingMatches()
    {
        SeedFood(Guid.CreateVersion7(), LegacyObjectId, "Apple");

        await _foods.CreateFoodAsync([new Food { Id = "5f8d0d55b54764421b7156ff", Name = "Banana" }]);

        (await ReadFoodsAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task CreateSettings_UpdatesTheRowHoldingTheKey_WhenTheIdIsUnrecognised()
    {
        // The unique index is (tenant_id, key), so an unmatched id must not become a second row.
        var storedId = SeedSetting(Guid.CreateVersion7(), null, "displayUnits", "\"mmol\"");

        await _settings.CreateSettingsAsync(
            [new Settings { Id = "settings-displayUnits", Key = "displayUnits", Value = "mg/dl" }]);

        var rows = await ReadSettingsAsync();
        rows.Should().ContainSingle();
        rows[0].Id.Should().Be(storedId);
        rows[0].Value.Should().Be("\"mg/dl\"");
    }

    [Fact]
    public async Task CreateSettings_UpdatesAMigratedRowInPlace_WhenTheLegacyIdComesBack()
    {
        var storedId = SeedSetting(PaddedKey(LegacyObjectId), LegacyObjectId, "displayUnits", "\"mmol\"");

        await _settings.CreateSettingsAsync(
            [new Settings { Id = LegacyObjectId, Key = "displayUnits", Value = "mg/dl" }]);

        var rows = await ReadSettingsAsync();
        rows.Should().ContainSingle();
        rows[0].Id.Should().Be(storedId);
        rows[0].OriginalId.Should().Be(LegacyObjectId);
    }

    [Fact]
    public async Task CreateSettings_FallsBackToTheLegacyId_WhenNoRowHoldsTheKey()
    {
        var storedId = SeedSetting(Guid.CreateVersion7(), LegacyObjectId, "displayUnits", "\"mmol\"");

        await _settings.CreateSettingsAsync(
            [new Settings { Id = LegacyObjectId, Key = "glucoseUnits", Value = "mg/dl" }]);

        var rows = await ReadSettingsAsync();
        rows.Should().ContainSingle("the legacy id names an existing row even when its key is being renamed");
        rows[0].Id.Should().Be(storedId);
        rows[0].Key.Should().Be("glucoseUnits");
        rows[0].Value.Should().Be("\"mg/dl\"");
    }

    [Fact]
    public async Task CreateSettings_InsertsANewKey()
    {
        SeedSetting(Guid.CreateVersion7(), null, "displayUnits", "\"mmol\"");

        await _settings.CreateSettingsAsync([new Settings { Id = "", Key = "theme", Value = "dark" }]);

        (await ReadSettingsAsync()).Select(s => s.Key).Should().BeEquivalentTo(["displayUnits", "theme"]);
    }
}
