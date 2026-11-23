using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Services.Connectors;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Connectors;

/// <summary>
/// Covers what a connector's repeated re-import of its whole lookback window is allowed to do to
/// entries it has already imported.
/// </summary>
[Trait("Category", "Unit")]
public class ConnectorFoodEntryServiceTests
{
    private const string Source = "myfitnesspal-connector";
    private const string UserId = "user-1";

    private readonly DbContextOptions<NocturneDbContext> _options;
    private readonly Mock<IMealMatchingService> _mealMatchingMock = new();
    private readonly Guid _tenantId = Guid.NewGuid();

    public ConnectorFoodEntryServiceTests()
    {
        _options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase($"connector_food_entry_tests_{Guid.NewGuid()}")
            .Options;

        using var db = NewContext();
        db.Database.EnsureCreated();
    }

    private NocturneDbContext NewContext() => new(_options) { TenantId = _tenantId };

    private ConnectorFoodEntryService NewService(NocturneDbContext context) =>
        new(new ConnectorFoodEntryRepository(context.AsFactory(), Mock.Of<ILogger<ConnectorFoodEntryRepository>>()),
            _mealMatchingMock.Object,
            TimeProvider.System,
            Mock.Of<ILogger<ConnectorFoodEntryService>>());

    private static ConnectorFoodEntryImport Import(
        string externalEntryId = "entry-1",
        string mealName = "Breakfast",
        bool isTimeInferred = true,
        DateTimeOffset? consumedAt = null,
        decimal carbs = 30) =>
        new()
        {
            ConnectorSource = Source,
            ExternalEntryId = externalEntryId,
            ExternalFoodId = "food-1",
            ConsumedAt = consumedAt ?? new DateTimeOffset(2026, 7, 20, 8, 0, 0, TimeSpan.Zero),
            MealName = mealName,
            IsTimeInferred = isTimeInferred,
            Carbs = carbs,
        };

    [Fact]
    public async Task ImportAsync_FoodTheUserDeleted_StaysDeleted_AndTheEntryIsNotLinkedToIt()
    {
        var food = await ImportThenDeleteFoodAsync(byUser: true);

        food.DeletedAt.Should().NotBeNull();
        await using var assertContext = NewContext();
        (await assertContext.Foods.IgnoreQueryFilters().CountAsync()).Should().Be(1);
        (await assertContext.ConnectorFoodEntries.SingleAsync()).FoodId.Should().BeNull();
    }

    [Fact]
    public async Task ImportAsync_FoodASystemSweepDeleted_IsRestoredOnItsOwnRow()
    {
        var food = await ImportThenDeleteFoodAsync(byUser: false);

        food.DeletedAt.Should().BeNull();
        food.Name.Should().Be("Oats");
        await using var assertContext = NewContext();
        (await assertContext.ConnectorFoodEntries.SingleAsync()).FoodId.Should().Be(food.Id);
    }

    /// <summary>
    /// Imports an entry with its food, soft-deletes the food as the user or a sweep would (the
    /// entry losing its link, as the food delete does), imports the same entry again, and returns
    /// the food row.
    /// </summary>
    private async Task<FoodEntity> ImportThenDeleteFoodAsync(bool byUser)
    {
        var withFood = Import();
        withFood.Food = new ConnectorFoodImport { ExternalId = "food-1", Name = "Oats", Carbs = 30 };

        await using (var context = NewContext())
        {
            await NewService(context).ImportAsync(UserId, [withFood]);
        }

        await using (var context = NewContext())
        {
            var stored = await context.Foods.SingleAsync();
            stored.DeletedAt = DateTime.UtcNow;
            context.Entry(stored).Property("DeletedByUser").CurrentValue = byUser;
            (await context.ConnectorFoodEntries.SingleAsync()).FoodId = null;
            await context.SaveChangesAsync();
        }

        await using (var context = NewContext())
        {
            await NewService(context).ImportAsync(UserId, [withFood]);
        }

        await using var readContext = NewContext();
        return await readContext.Foods.IgnoreQueryFilters().SingleAsync();
    }

    [Fact]
    public async Task ImportAsync_DoesNotReplaceANamedMealWithAnUnnamedGuess()
    {
        var breakfast = new DateTimeOffset(2026, 7, 20, 8, 0, 0, TimeSpan.Zero);
        var midday = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

        await using (var context = NewContext())
        {
            await NewService(context).ImportAsync(UserId, [Import(consumedAt: breakfast)]);
        }

        // The next cycle failed to attribute the day, so it can only offer midday and no meal name.
        await using (var context = NewContext())
        {
            await NewService(context).ImportAsync(
                UserId,
                [Import(mealName: string.Empty, consumedAt: midday, carbs: 45)]);
        }

        await using var assertContext = NewContext();
        var stored = await assertContext.ConnectorFoodEntries.SingleAsync();

        stored.ConsumedAt.Should().Be(breakfast, "an inferred time must not overwrite a better one");
        stored.MealName.Should().Be("Breakfast");
        stored.Carbs.Should().Be(45, "nutrition is reported directly and still refreshes");
    }

    [Fact]
    public async Task ImportAsync_AppliesAnInferredTimeThatRecoversTheMealName()
    {
        var midday = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        var breakfast = new DateTimeOffset(2026, 7, 20, 8, 0, 0, TimeSpan.Zero);

        await using (var context = NewContext())
        {
            await NewService(context).ImportAsync(
                UserId,
                [Import(mealName: string.Empty, consumedAt: midday)]);
        }

        await using (var context = NewContext())
        {
            await NewService(context).ImportAsync(UserId, [Import(consumedAt: breakfast)]);
        }

        await using var assertContext = NewContext();
        var stored = await assertContext.ConnectorFoodEntries.SingleAsync();

        stored.ConsumedAt.Should().Be(breakfast);
        stored.MealName.Should().Be("Breakfast");
    }

    [Fact]
    public async Task ImportAsync_AlwaysAppliesAReportedTime()
    {
        var inferred = new DateTimeOffset(2026, 7, 20, 8, 0, 0, TimeSpan.Zero);
        var reported = new DateTimeOffset(2026, 7, 20, 9, 42, 0, TimeSpan.Zero);

        await using (var context = NewContext())
        {
            await NewService(context).ImportAsync(UserId, [Import(consumedAt: inferred)]);
        }

        await using (var context = NewContext())
        {
            await NewService(context).ImportAsync(
                UserId,
                [Import(mealName: string.Empty, isTimeInferred: false, consumedAt: reported)]);
        }

        await using var assertContext = NewContext();
        var stored = await assertContext.ConnectorFoodEntries.SingleAsync();

        stored.ConsumedAt.Should().Be(reported);
    }

    [Fact]
    public async Task ImportAsync_OnlyHandsNewlyCreatedEntriesToMealMatching()
    {
        await using (var context = NewContext())
        {
            await NewService(context).ImportAsync(UserId, [Import()]);
        }

        _mealMatchingMock.Invocations.Clear();

        // The same window read again: nothing new, so matching must not run at all.
        await using (var context = NewContext())
        {
            await NewService(context).ImportAsync(UserId, [Import()]);
        }

        _mealMatchingMock.Verify(
            m => m.ProcessNewFoodEntriesAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // A genuinely new entry alongside the existing one hands over only the new one.
        await using (var newEntryContext = NewContext())
        {
            await NewService(newEntryContext).ImportAsync(UserId, [Import(), Import("entry-2")]);
        }

        await using var assertContext = NewContext();
        var newId = await assertContext.ConnectorFoodEntries
            .Where(e => e.ExternalEntryId == "entry-2")
            .Select(e => e.Id)
            .SingleAsync();

        _mealMatchingMock.Verify(
            m => m.ProcessNewFoodEntriesAsync(
                UserId,
                It.Is<IEnumerable<Guid>>(ids => ids.SequenceEqual(new[] { newId })),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ImportAsync_ReMatchesAnEntryWhoseConsumedTimeWasCorrected()
    {
        var midday = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        var breakfast = new DateTimeOffset(2026, 7, 20, 8, 0, 0, TimeSpan.Zero);

        await using (var context = NewContext())
        {
            await NewService(context).ImportAsync(
                UserId,
                [Import(mealName: string.Empty, consumedAt: midday)]);
        }

        _mealMatchingMock.Invocations.Clear();

        // Attribution succeeded this cycle, so the entry now matches against a different time.
        // Matching keys off that time, so leaving it out would strand the correction.
        await using (var context = NewContext())
        {
            await NewService(context).ImportAsync(UserId, [Import(consumedAt: breakfast)]);
        }

        await using var assertContext = NewContext();
        var id = await assertContext.ConnectorFoodEntries.Select(e => e.Id).SingleAsync();

        _mealMatchingMock.Verify(
            m => m.ProcessNewFoodEntriesAsync(
                UserId,
                It.Is<IEnumerable<Guid>>(ids => ids.SequenceEqual(new[] { id })),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ImportAsync_RestoresAWithdrawnEntryTheConnectorReportsAgain()
    {
        await using (var context = NewContext())
        {
            await NewService(context).ImportAsync(UserId, [Import()]);
        }

        await using (var context = NewContext())
        {
            var entry = await context.ConnectorFoodEntries.SingleAsync();
            entry.Status = ConnectorFoodEntryStatus.Deleted;
            entry.ResolvedAt = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync();
        }

        _mealMatchingMock.Invocations.Clear();

        // Nothing else in the codebase returns a record to Pending, so a withdrawal that turns out
        // to be wrong has to be undone here or the entry is lost for good.
        await using (var context = NewContext())
        {
            await NewService(context).ImportAsync(UserId, [Import()]);
        }

        await using var assertContext = NewContext();
        var stored = await assertContext.ConnectorFoodEntries.SingleAsync();

        stored.Status.Should().Be(ConnectorFoodEntryStatus.Pending);
        stored.ResolvedAt.Should().BeNull();

        _mealMatchingMock.Verify(
            m => m.ProcessNewFoodEntriesAsync(
                UserId,
                It.Is<IEnumerable<Guid>>(ids => ids.SequenceEqual(new[] { stored.Id })),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private ConnectorFoodEntryService NewServiceOver(Mock<IConnectorFoodEntryRepository> entries) =>
        new(entries.Object,
            _mealMatchingMock.Object,
            TimeProvider.System,
            Mock.Of<ILogger<ConnectorFoodEntryService>>());

    [Fact]
    public async Task MarkMissingAsDeletedAsync_WithdrawsTheSuggestionOfEachEntryTheRepositoryWithdrew()
    {
        var from = new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero);
        var withdrawn = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var entries = new Mock<IConnectorFoodEntryRepository>();
        entries
            .Setup(r => r.MarkMissingAsDeletedAsync(
                Source,
                from,
                to,
                It.Is<IReadOnlySet<string>>(present => present.SetEquals(new[] { "kept" })),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(withdrawn);

        var count = await NewServiceOver(entries).MarkMissingAsDeletedAsync(UserId, Source, from, to, ["kept"]);

        count.Should().Be(2);
        foreach (var id in withdrawn)
        {
            _mealMatchingMock.Verify(
                m => m.WithdrawSuggestionAsync(UserId, id, It.IsAny<CancellationToken>()),
                Times.Once);
        }
    }

    [Fact]
    public async Task MarkMissingAsDeletedAsync_WithdrawsNoSuggestionForAnEntryTheRepositoryLeftAlone()
    {
        var entries = new Mock<IConnectorFoodEntryRepository>();
        entries
            .Setup(r => r.MarkMissingAsDeletedAsync(
                It.IsAny<string>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<IReadOnlySet<string>>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var count = await NewServiceOver(entries).MarkMissingAsDeletedAsync(
            UserId, Source, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, []);

        count.Should().Be(0);
        _mealMatchingMock.Verify(
            m => m.WithdrawSuggestionAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
