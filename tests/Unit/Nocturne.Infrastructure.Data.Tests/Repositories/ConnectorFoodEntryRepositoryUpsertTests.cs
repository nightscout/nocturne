using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// An import writes foods and the entries that reference them together, and a legacy treatment is
/// served with the name of its food, so a change to an existing food is reported once the commit
/// holds.
/// </summary>
[Trait("Category", "Unit")]
public class ConnectorFoodEntryRepositoryUpsertTests : IDisposable
{
    private const string Source = "myfitnesspal-connector";
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly List<Type> _written = [];
    private readonly FailingSaveInterceptor _failingSave = new();
    private readonly ConnectorFoodEntryRepository _repository;

    public ConnectorFoodEntryRepositoryUpsertTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(TenantId, "test", _failingSave);
        _context = _db.CreateContext();
        var legacyReads = new Mock<ILegacyReadCacheInvalidation>();
        legacyReads
            .Setup(r => r.WrittenAsync(It.IsAny<IReadOnlyCollection<Type>>()))
            .Callback((IReadOnlyCollection<Type> models) => _written.AddRange(models))
            .Returns(Task.CompletedTask);
        _repository = new ConnectorFoodEntryRepository(
            _context.AsFactory(), NullLogger<ConnectorFoodEntryRepository>.Instance, legacyReads.Object);
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private static ConnectorFoodEntryImport Import(string foodName = "apple", string entryId = "entry-1") => new()
    {
        ConnectorSource = Source,
        ExternalEntryId = entryId,
        ExternalFoodId = "food-1",
        ConsumedAt = new DateTimeOffset(2026, 7, 20, 8, 0, 0, TimeSpan.Zero),
        Carbs = 10,
        Food = new ConnectorFoodImport { ExternalId = "food-1", Name = foodName, Carbs = 10 },
    };

    private async Task SoftDeleteFoodAsync(bool byUser)
    {
        var food = await _context.Foods.SingleAsync();
        food.DeletedAt = DateTime.UtcNow;
        _context.Entry(food).Property("DeletedByUser").CurrentValue = byUser;
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    [Fact]
    public async Task AnImport_WritesTheFoodAndTheEntryLinkedToIt()
    {
        var upsert = await _repository.UpsertAsync([Import()]);

        var food = await _context.Foods.AsNoTracking().SingleAsync();
        var entry = await _context.ConnectorFoodEntries.AsNoTracking().SingleAsync();
        entry.FoodId.Should().Be(food.Id);
        upsert.Entries.Should().ContainSingle().Which.Id.Should().Be(entry.Id);
        upsert.EntriesToMatch.Should().Equal(entry.Id);
    }

    [Fact]
    public async Task AFoodRepeatedInOneBatch_IsWrittenOnce()
    {
        await _repository.UpsertAsync([Import(entryId: "entry-1"), Import(entryId: "entry-2")]);

        (await _context.Foods.AsNoTracking().CountAsync()).Should().Be(1);
        (await _context.ConnectorFoodEntries.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task AnEntryThatCannotBeSaved_LeavesNoFoodBehind()
    {
        _failingSave.Armed = true;

        var import = () => _repository.UpsertAsync([Import()]);

        await import.Should().ThrowAsync<InvalidOperationException>();
        _failingSave.Armed = false;
        (await _context.Foods.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await _context.ConnectorFoodEntries.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        _written.Should().BeEmpty();
    }

    [Fact]
    public async Task ARenameThatCannotBeSaved_ReportsNothingAndKeepsTheName()
    {
        await _repository.UpsertAsync([Import("apple")]);
        _failingSave.Armed = true;

        var import = () => _repository.UpsertAsync([Import("pear", entryId: "entry-2")]);

        await import.Should().ThrowAsync<InvalidOperationException>();
        _failingSave.Armed = false;
        (await _context.Foods.AsNoTracking().SingleAsync()).Name.Should().Be("apple");
        _written.Should().BeEmpty("the rename never committed");
    }

    [Fact]
    public async Task ANewFood_ReportsNothing()
    {
        await _repository.UpsertAsync([Import()]);

        _written.Should().BeEmpty("no treatment points at a food that did not exist");
    }

    [Fact]
    public async Task ARenamedFood_RenamesTheRowAndReportsFoodsAfterTheCommit()
    {
        await _repository.UpsertAsync([Import("apple")]);

        await _repository.UpsertAsync([Import("pear")]);

        (await _context.Foods.AsNoTracking().SingleAsync()).Name.Should().Be("pear");
        _written.Should().Equal(typeof(Food));
    }

    [Fact]
    public async Task AFoodASystemSweepDeleted_IsRestoredOnItsOwnRowAndReportsFoods()
    {
        await _repository.UpsertAsync([Import()]);
        var id = (await _context.Foods.AsNoTracking().SingleAsync()).Id;
        await SoftDeleteFoodAsync(byUser: false);

        await _repository.UpsertAsync([Import()]);

        var food = await _context.Foods.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        food.Id.Should().Be(id);
        food.DeletedAt.Should().BeNull();
        _written.Should().Equal(typeof(Food));
    }

    [Fact]
    public async Task AFoodTheUserDeleted_StaysDeletedAndTheEntryIsNotLinkedToIt()
    {
        await _repository.UpsertAsync([Import()]);
        await SoftDeleteFoodAsync(byUser: true);
        (await _context.ConnectorFoodEntries.SingleAsync()).FoodId = null;
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        await _repository.UpsertAsync([Import("pear")]);

        var food = await _context.Foods.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        food.DeletedAt.Should().NotBeNull();
        food.Name.Should().Be("apple");
        (await _context.ConnectorFoodEntries.AsNoTracking().SingleAsync()).FoodId.Should().BeNull();
        _written.Should().BeEmpty();
    }

    [Fact]
    public async Task AReimportThatChangesNoFood_ReportsNothing()
    {
        await _repository.UpsertAsync([Import()]);

        var upsert = await _repository.UpsertAsync([Import()]);

        _written.Should().BeEmpty();
        upsert.EntriesToMatch.Should().BeEmpty("the entry is already pending and its time did not move");
    }

    [Fact]
    public async Task ImportsNamingNoSourceOrNoEntryId_AreSkipped()
    {
        var noSource = Import();
        noSource.ConnectorSource = " ";
        var noEntryId = Import();
        noEntryId.ExternalEntryId = "";

        var upsert = await _repository.UpsertAsync([noSource, noEntryId]);

        upsert.Entries.Should().BeEmpty();
        (await _context.Foods.AsNoTracking().CountAsync()).Should().Be(0);
        (await _context.ConnectorFoodEntries.CountAsync()).Should().Be(0);
    }

    private sealed class FailingSaveInterceptor : SaveChangesInterceptor
    {
        /// <summary>Refuses a save that inserts an entry, so a food saved before it would survive.</summary>
        public bool Armed { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            Armed && eventData.Context!.ChangeTracker.Entries<ConnectorFoodEntryEntity>().Any(e => e.State == EntityState.Added)
                ? throw new InvalidOperationException("save refused")
                : base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}
