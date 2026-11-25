using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// Accepting a match claims the food entry and writes its breakdown row together, so a food entry is
/// linked to one carb intake once however many accepts reach it.
/// </summary>
[Trait("Category", "Unit")]
public class ConnectorFoodEntryRepositoryAcceptMatchTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateTime Written = new(2026, 7, 20, 8, 0, 0, DateTimeKind.Utc);

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly List<Type> _written = [];
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 7, 21, 9, 30, 0, TimeSpan.Zero));
    private readonly ConnectorFoodEntryRepository _repository;
    private readonly TreatmentFoodRepository _breakdowns;

    public ConnectorFoodEntryRepositoryAcceptMatchTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(TenantId, "test");
        _context = _db.CreateContext();
        var legacyReads = new Mock<ILegacyReadCacheInvalidation>();
        legacyReads
            .Setup(r => r.WrittenAsync(It.IsAny<IReadOnlyCollection<Type>>()))
            .Callback((IReadOnlyCollection<Type> models) => _written.AddRange(models))
            .Returns(Task.CompletedTask);
        _repository = new ConnectorFoodEntryRepository(
            _context.AsFactory(), NullLogger<ConnectorFoodEntryRepository>.Instance, legacyReads.Object, _time);
        _breakdowns = new TreatmentFoodRepository(_context.AsFactory());
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<Guid> SeedCarbIntakeAsync()
    {
        var id = Guid.CreateVersion7();
        _context.CarbIntakes.Add(new CarbIntakeEntity { Id = id, TenantId = TenantId, Timestamp = Written, Carbs = 40 });
        await _context.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> SeedEntryAsync(
        ConnectorFoodEntryStatus status = ConnectorFoodEntryStatus.Pending, Guid? matchedCarbIntakeId = null)
    {
        var id = Guid.CreateVersion7();
        _context.ConnectorFoodEntries.Add(new ConnectorFoodEntryEntity
        {
            Id = id,
            TenantId = TenantId,
            ConnectorSource = "test-connector",
            ExternalEntryId = id.ToString(),
            ExternalFoodId = "food-1",
            ConsumedAt = Written,
            Carbs = 12,
            Servings = 1,
            Status = status,
            MatchedCarbIntakeId = matchedCarbIntakeId,
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return id;
    }

    private static TreatmentFood Row(Guid carbIntakeId) => new()
    {
        Id = Guid.CreateVersion7(),
        CarbIntakeId = carbIntakeId,
        Portions = 1,
        Carbs = 12,
    };

    private async Task<ConnectorFoodEntryEntity> StoredAsync(Guid id) =>
        await _context.ConnectorFoodEntries.AsNoTracking().SingleAsync(e => e.Id == id);

    private async Task<decimal> BreakdownCarbsAsync(Guid carbIntakeId) =>
        (await _breakdowns.GetByCarbIntakeIdAsync(carbIntakeId)).Sum(f => f.Carbs);

    [Fact]
    public async Task AcceptingAPendingEntry_LinksItAndRecordsTheCarbIntake()
    {
        var carbIntake = await SeedCarbIntakeAsync();
        var entry = await SeedEntryAsync();

        var outcome = await _repository.AcceptMatchAsync(entry, Row(carbIntake));

        outcome.Should().Be(AcceptMatchOutcome.Linked);
        var stored = await StoredAsync(entry);
        stored.Status.Should().Be(ConnectorFoodEntryStatus.Matched);
        stored.MatchedCarbIntakeId.Should().Be(carbIntake);
        stored.ResolvedAt.Should().NotBeNull();
        (await BreakdownCarbsAsync(carbIntake)).Should().Be(12);
        _written.Should().Equal(typeof(TreatmentFood));
    }

    [Fact]
    public async Task AcceptingTheSameEntryTwiceForOneCarbIntake_LeavesOneRowAndTheBreakdownUnchanged()
    {
        var carbIntake = await SeedCarbIntakeAsync();
        var entry = await SeedEntryAsync();
        await _repository.AcceptMatchAsync(entry, Row(carbIntake));
        var before = await BreakdownCarbsAsync(carbIntake);
        _written.Clear();

        var second = await _repository.AcceptMatchAsync(entry, Row(carbIntake));

        second.Should().Be(AcceptMatchOutcome.AlreadyLinked);
        (await _breakdowns.GetByCarbIntakeIdAsync(carbIntake)).Should().ContainSingle();
        (await BreakdownCarbsAsync(carbIntake)).Should().Be(before);
        _written.Should().BeEmpty("nothing was written, so no legacy read is stale");
    }

    [Fact]
    public async Task AnEntryMatchedToAnotherCarbIntake_IsRefusedAndWritesNothing()
    {
        var first = await SeedCarbIntakeAsync();
        var other = await SeedCarbIntakeAsync();
        var entry = await SeedEntryAsync();
        await _repository.AcceptMatchAsync(entry, Row(first));
        _written.Clear();

        var outcome = await _repository.AcceptMatchAsync(entry, Row(other));

        outcome.Should().Be(AcceptMatchOutcome.AlreadyResolved);
        (await _breakdowns.GetByCarbIntakeIdAsync(other)).Should().BeEmpty();
        (await _breakdowns.GetByCarbIntakeIdAsync(first)).Should().ContainSingle();
        (await StoredAsync(entry)).MatchedCarbIntakeId.Should().Be(first);
        _written.Should().BeEmpty();
    }

    [Fact]
    public async Task AnEntryMatchedBeforeTheCarbIntakeWasRecorded_IsRefusedForEveryCarbIntake()
    {
        var carbIntake = await SeedCarbIntakeAsync();
        var entry = await SeedEntryAsync(ConnectorFoodEntryStatus.Matched);

        var outcome = await _repository.AcceptMatchAsync(entry, Row(carbIntake));

        outcome.Should().Be(AcceptMatchOutcome.AlreadyResolved);
        (await _breakdowns.GetByCarbIntakeIdAsync(carbIntake)).Should().BeEmpty();
    }

    [Theory]
    [InlineData(ConnectorFoodEntryStatus.Standalone)]
    [InlineData(ConnectorFoodEntryStatus.Deleted)]
    public async Task ADismissedOrWithdrawnEntry_IsRefusedAndWritesNothing(ConnectorFoodEntryStatus status)
    {
        var carbIntake = await SeedCarbIntakeAsync();
        var entry = await SeedEntryAsync(status);

        var outcome = await _repository.AcceptMatchAsync(entry, Row(carbIntake));

        outcome.Should().Be(AcceptMatchOutcome.AlreadyResolved);
        (await _breakdowns.GetByCarbIntakeIdAsync(carbIntake)).Should().BeEmpty();
        (await StoredAsync(entry)).Status.Should().Be(status);
    }

    [Fact]
    public async Task AnEntryThatDoesNotExist_ReportsNotFoundAndWritesNothing()
    {
        var carbIntake = await SeedCarbIntakeAsync();

        var outcome = await _repository.AcceptMatchAsync(Guid.NewGuid(), Row(carbIntake));

        outcome.Should().Be(AcceptMatchOutcome.NotFound);
        (await _breakdowns.GetByCarbIntakeIdAsync(carbIntake)).Should().BeEmpty();
    }

    [Fact]
    public async Task ABreakdownRowWrittenOnAUnit_IsRolledBackWithIt()
    {
        var carbIntake = await SeedCarbIntakeAsync();
        await using var unit = await DbUnitOfWork.OpenAsync(_context.AsFactory());
        var onTheUnit = new TreatmentFoodRepository(unit);

        var failing = () => unit.CommitAsync(async token =>
        {
            await onTheUnit.CreateAsync(Row(carbIntake), token);
            throw new InvalidOperationException("the owner's work failed after the write");
        });

        await failing.Should().ThrowAsync<InvalidOperationException>();
        (await _breakdowns.GetByCarbIntakeIdAsync(carbIntake)).Should().BeEmpty();
    }

    [Fact]
    public async Task DismissingAPendingEntry_MarksItStandaloneAtTheTimeProvidersTime()
    {
        var entry = await SeedEntryAsync();

        var outcome = await _repository.DismissAsync(entry);

        outcome.Should().Be(DismissMatchOutcome.Dismissed);
        var stored = await StoredAsync(entry);
        stored.Status.Should().Be(ConnectorFoodEntryStatus.Standalone);
        stored.MatchedCarbIntakeId.Should().BeNull();
        stored.ResolvedAt.Should().Be(_time.GetUtcNow());
    }

    [Fact]
    public async Task DismissingAnEntryAnAcceptLinked_LeavesItMatchedToItsCarbIntakeAndItsBreakdown()
    {
        var carbIntake = await SeedCarbIntakeAsync();
        var entry = await SeedEntryAsync();
        await _repository.AcceptMatchAsync(entry, Row(carbIntake));
        var linked = await StoredAsync(entry);

        var outcome = await _repository.DismissAsync(entry);

        outcome.Should().Be(DismissMatchOutcome.AlreadyResolved);
        var stored = await StoredAsync(entry);
        stored.Status.Should().Be(ConnectorFoodEntryStatus.Matched);
        stored.MatchedCarbIntakeId.Should().Be(carbIntake);
        stored.ResolvedAt.Should().Be(linked.ResolvedAt);
        (await _breakdowns.GetByCarbIntakeIdAsync(carbIntake)).Should().ContainSingle();
    }

    [Fact]
    public async Task DismissingAWithdrawnEntry_LeavesItWithdrawn()
    {
        var entry = await SeedEntryAsync(ConnectorFoodEntryStatus.Deleted);

        var outcome = await _repository.DismissAsync(entry);

        outcome.Should().Be(DismissMatchOutcome.AlreadyResolved);
        (await StoredAsync(entry)).Status.Should().Be(ConnectorFoodEntryStatus.Deleted);
    }

    [Fact]
    public async Task DismissingAnEntryTwice_ReportsTheRepeatAndKeepsTheFirstResolvedTime()
    {
        var entry = await SeedEntryAsync();
        await _repository.DismissAsync(entry);
        var first = (await StoredAsync(entry)).ResolvedAt;
        _time.Advance(TimeSpan.FromMinutes(5));

        var second = await _repository.DismissAsync(entry);

        second.Should().Be(DismissMatchOutcome.AlreadyDismissed);
        (await StoredAsync(entry)).ResolvedAt.Should().Be(first);
    }

    [Fact]
    public async Task DismissingAnEntryThatDoesNotExist_ReportsNotFound()
    {
        (await _repository.DismissAsync(Guid.NewGuid())).Should().Be(DismissMatchOutcome.NotFound);
    }

    [Fact]
    public async Task AnEntryTheConnectorReportsAgainAfterAWithdrawal_ComesBackPendingWithNoCarbIntake()
    {
        var carbIntake = await SeedCarbIntakeAsync();
        var entry = await SeedEntryAsync(ConnectorFoodEntryStatus.Deleted, matchedCarbIntakeId: carbIntake);
        var stored = await StoredAsync(entry);

        await _repository.UpsertAsync([new ConnectorFoodEntryImport
        {
            ConnectorSource = stored.ConnectorSource,
            ExternalEntryId = stored.ExternalEntryId,
            ExternalFoodId = stored.ExternalFoodId,
            ConsumedAt = stored.ConsumedAt,
            Carbs = 12,
            Servings = 1,
        }]);

        var restored = await StoredAsync(entry);
        restored.Status.Should().Be(ConnectorFoodEntryStatus.Pending);
        restored.MatchedCarbIntakeId.Should().BeNull();
        (await _repository.AcceptMatchAsync(entry, Row(await SeedCarbIntakeAsync())))
            .Should().Be(AcceptMatchOutcome.Linked);
    }
}
