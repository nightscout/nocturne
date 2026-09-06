using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Services.Connectors;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
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
        new(context,
            _mealMatchingMock.Object,
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
        var from = new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero);

        await using (var context = NewContext())
        {
            await NewService(context).ImportAsync(UserId, [Import()]);
        }

        await using (var context = NewContext())
        {
            await NewService(context).MarkMissingAsDeletedAsync(UserId, Source, from, to, []);
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

    [Fact]
    public async Task MarkMissingAsDeletedAsync_WithdrawsPendingEntriesTheConnectorNoLongerReports()
    {
        var from = new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero);

        await using (var context = NewContext())
        {
            await NewService(context).ImportAsync(UserId, [Import("kept"), Import("deleted-upstream")]);
        }

        Guid withdrawnId;
        await using (var context = NewContext())
        {
            withdrawnId = await context.ConnectorFoodEntries
                .Where(e => e.ExternalEntryId == "deleted-upstream")
                .Select(e => e.Id)
                .SingleAsync();

            var withdrawn = await NewService(context)
                .MarkMissingAsDeletedAsync(UserId, Source, from, to, ["kept"]);

            withdrawn.Should().Be(1);
        }

        await using var assertContext = NewContext();
        var entries = await assertContext.ConnectorFoodEntries.ToListAsync();

        entries.Single(e => e.ExternalEntryId == "kept").Status
            .Should().Be(ConnectorFoodEntryStatus.Pending);

        var gone = entries.Single(e => e.ExternalEntryId == "deleted-upstream");
        gone.Status.Should().Be(ConnectorFoodEntryStatus.Deleted);
        gone.ResolvedAt.Should().NotBeNull();

        _mealMatchingMock.Verify(
            m => m.WithdrawSuggestionAsync(UserId, withdrawnId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task MarkMissingAsDeletedAsync_LeavesResolvedEntriesAndOtherWindowsAlone()
    {
        var from = new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero);

        await using (var seed = NewContext())
        {
            seed.ConnectorFoodEntries.AddRange(
                new ConnectorFoodEntryEntity
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = _tenantId,
                    ConnectorSource = Source,
                    ExternalEntryId = "already-matched",
                    ConsumedAt = from.AddHours(8),
                    Status = ConnectorFoodEntryStatus.Matched,
                },
                new ConnectorFoodEntryEntity
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = _tenantId,
                    ConnectorSource = Source,
                    ExternalEntryId = "outside-window",
                    ConsumedAt = from.AddDays(-9),
                    Status = ConnectorFoodEntryStatus.Pending,
                },
                new ConnectorFoodEntryEntity
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = _tenantId,
                    ConnectorSource = "glooko-connector",
                    ExternalEntryId = "other-connector",
                    ConsumedAt = from.AddHours(8),
                    Status = ConnectorFoodEntryStatus.Pending,
                });
            await seed.SaveChangesAsync();
        }

        await using (var context = NewContext())
        {
            var withdrawn = await NewService(context)
                .MarkMissingAsDeletedAsync(UserId, Source, from, to, []);

            withdrawn.Should().Be(0);
        }

        await using var assertContext = NewContext();
        var entries = await assertContext.ConnectorFoodEntries.ToListAsync();

        entries.Should().OnlyContain(e => e.Status != ConnectorFoodEntryStatus.Deleted);
    }
}
