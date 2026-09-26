using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Connectors;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Configuration;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Services.Treatments;

/// <summary>
/// Covers how a batch of food entries survives notification failures, and that one entry never
/// raises more than one live suggestion.
/// </summary>
[Trait("Category", "Unit")]
public class MealMatchingServiceTests
{
    private const string UserId = "user-1";

    private readonly Mock<IConnectorFoodEntryRepository> _foodEntryRepository = new();
    private readonly Mock<ICarbIntakeRepository> _carbIntakeRepository = new();
    private readonly Mock<ITreatmentFoodService> _treatmentFoodService = new();
    private readonly Mock<IInAppNotificationService> _notificationService = new();
    private readonly Mock<IInAppNotificationRepository> _notificationRepository = new();
    private readonly Mock<IMyFitnessPalMatchingSettingsService> _settingsService = new();

    private static readonly DateTimeOffset ConsumedAt = new(2026, 7, 20, 8, 0, 0, TimeSpan.Zero);

    public MealMatchingServiceTests()
    {
        _settingsService
            .Setup(s => s.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MyFitnessPalMatchingSettings());

        // Every entry has a carb intake it matches, so each one wants to raise a notification.
        _carbIntakeRepository
            .Setup(s => s.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new CarbIntake
            {
                Id = Guid.CreateVersion7(),
                Carbs = 30,
                Timestamp = ConsumedAt.UtcDateTime,
            }]);
    }

    private MealMatchingService NewService() =>
        new(_foodEntryRepository.Object,
            _carbIntakeRepository.Object,
            _treatmentFoodService.Object,
            _notificationService.Object,
            _notificationRepository.Object,
            _settingsService.Object,
            Mock.Of<ILogger<MealMatchingService>>());

    /// <summary>
    /// The same service over a settings store that can no longer be read, which is what a failed
    /// read looks like when it is produced rather than stubbed.
    /// </summary>
    private async Task<MealMatchingService> NewServiceOverABrokenSettingsStoreAsync()
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var context = new NocturneDbContext(options)
        {
            TenantId = Guid.Parse("77777777-7777-7777-7777-777777777777"),
        };
        var settings = new MyFitnessPalMatchingSettingsService(
            context,
            NullLogger<MyFitnessPalMatchingSettingsService>.Instance);
        await context.DisposeAsync();

        return new MealMatchingService(
            _foodEntryRepository.Object,
            _carbIntakeRepository.Object,
            _treatmentFoodService.Object,
            _notificationService.Object,
            _notificationRepository.Object,
            settings,
            Mock.Of<ILogger<MealMatchingService>>());
    }

    private static ConnectorFoodEntry Entry(Guid id) => new()
    {
        Id = id,
        ConnectorSource = "myfitnesspal-connector",
        MealName = "Breakfast",
        Carbs = 30,
        ConsumedAt = ConsumedAt,
        Status = ConnectorFoodEntryStatus.Pending,
    };

    /// <summary>
    /// The production read path, not a stubbed null: a store that cannot be read leaves matching
    /// with no tenant answer at all, and the defaults it would otherwise fall back to have
    /// notifications on and a 30-minute window.
    /// </summary>
    [Fact]
    public async Task MatchingOverAnUnreadableSettingsStore_neitherNotifiesNorSuggests()
    {
        var id = Guid.NewGuid();
        _foodEntryRepository
            .Setup(r => r.GetByIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Entry(id)]);
        _foodEntryRepository
            .Setup(r => r.GetPendingInTimeRangeAsync(
                It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Entry(id)]);
        var service = await NewServiceOverABrokenSettingsStoreAsync();

        await service.ProcessNewFoodEntriesAsync(UserId, [id]);

        _notificationService.Verify(
            n => n.CreateNotificationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<NotificationCategory?>(), It.IsAny<NotificationUrgency?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<List<NotificationActionDto>?>(), It.IsAny<ResolutionConditions?>(),
                It.IsAny<Dictionary<string, object>?>(), It.IsAny<CancellationToken>()),
            Times.Never);

        await service
            .Invoking(s => s.GetSuggestionsAsync(ConsumedAt.AddHours(-1), ConsumedAt.AddHours(1)))
            .Should()
            .ThrowAsync<SettingsUnavailableException>();
    }

    /// <summary>
    /// A failed settings read is not a tenant who left the defaults: notifications default to on
    /// and the match window to 30 minutes, so processing on them notifies a tenant who switched
    /// notifications off and offers matches their own window excludes.
    /// </summary>
    [Fact]
    public async Task ProcessNewFoodEntriesAsync_RaisesNothingWhenTheSettingsReadFails()
    {
        _settingsService
            .Setup(s => s.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((MyFitnessPalMatchingSettings?)null);
        var id = Guid.NewGuid();
        _foodEntryRepository
            .Setup(r => r.GetByIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Entry(id)]);

        await NewService().ProcessNewFoodEntriesAsync(UserId, [id]);

        _notificationService.Verify(
            n => n.CreateNotificationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<NotificationCategory?>(), It.IsAny<NotificationUrgency?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<List<NotificationActionDto>?>(), It.IsAny<ResolutionConditions?>(),
                It.IsAny<Dictionary<string, object>?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// <see cref="MealMatchingService.GetSuggestionsAsync"/> scores against the tenant's own match
    /// window and tolerances, and every suggestion it returns is one the reader can accept into
    /// <c>treatment_foods</c>. Scoring on defaults would offer matches to accept that the tenant's
    /// settings rule out, so the failure is reported rather than answered.
    /// </summary>
    [Fact]
    public async Task GetSuggestionsAsync_reportsTheFailedSettingsReadRatherThanScoringOnDefaults()
    {
        _settingsService
            .Setup(s => s.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((MyFitnessPalMatchingSettings?)null);
        _foodEntryRepository
            .Setup(r => r.GetPendingInTimeRangeAsync(
                It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Entry(Guid.NewGuid())]);

        await NewService()
            .Invoking(s => s.GetSuggestionsAsync(ConsumedAt.AddHours(-1), ConsumedAt.AddHours(1)))
            .Should()
            .ThrowAsync<SettingsUnavailableException>();
    }

    [Fact]
    public async Task ProcessNewFoodEntriesAsync_KeepsGoingWhenOneEntryCannotBeNotified()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        _foodEntryRepository
            .Setup(r => r.GetByIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ids.Select(Entry).ToList());

        // Before this was guarded, one failed notification abandoned the rest of the batch, so
        // matching silently stopped partway through.
        _notificationService
            .Setup(n => n.CreateNotificationAsync(
                UserId, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<NotificationCategory?>(),
                It.IsAny<NotificationUrgency?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), ids[0].ToString(), It.IsAny<List<NotificationActionDto>?>(),
                It.IsAny<ResolutionConditions?>(), It.IsAny<Dictionary<string, object>?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("notification write failed"));

        await NewService().ProcessNewFoodEntriesAsync(UserId, ids);

        foreach (var id in ids.Skip(1))
        {
            _notificationService.Verify(
                n => n.CreateNotificationAsync(
                    UserId, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<NotificationCategory?>(),
                    It.IsAny<NotificationUrgency?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                    It.IsAny<string?>(), id.ToString(), It.IsAny<List<NotificationActionDto>?>(),
                    It.IsAny<ResolutionConditions?>(), It.IsAny<Dictionary<string, object>?>(),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }
    }

    [Fact]
    public async Task ProcessNewFoodEntriesAsync_DoesNotStackASecondSuggestionOnTheSameEntry()
    {
        var id = Guid.NewGuid();
        _foodEntryRepository
            .Setup(r => r.GetByIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Entry(id)]);

        _notificationRepository
            .Setup(r => r.FindBySourceAsync(
                UserId, "meal_matching.suggested_match", id.ToString(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InAppNotificationEntity { SourceId = id.ToString() });

        await NewService().ProcessNewFoodEntriesAsync(UserId, [id]);

        _notificationService.Verify(
            n => n.CreateNotificationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<NotificationCategory?>(), It.IsAny<NotificationUrgency?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<List<NotificationActionDto>?>(), It.IsAny<ResolutionConditions?>(),
                It.IsAny<Dictionary<string, object>?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ProcessNewFoodEntriesAsync_RaisesOneSuggestionForAnEntryWithNoneYet()
    {
        var id = Guid.NewGuid();
        _foodEntryRepository
            .Setup(r => r.GetByIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Entry(id)]);

        await NewService().ProcessNewFoodEntriesAsync(UserId, [id]);

        _notificationService.Verify(
            n => n.CreateNotificationAsync(
                UserId, "meal_matching.suggested_match", It.IsAny<string>(),
                It.IsAny<NotificationCategory?>(), It.IsAny<NotificationUrgency?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), id.ToString(),
                It.IsAny<List<NotificationActionDto>?>(), It.IsAny<ResolutionConditions?>(),
                It.IsAny<Dictionary<string, object>?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// The suggestion carries the carb intake's own id, which is the key
    /// <see cref="MealMatchingService.AcceptMatchAsync"/> writes to
    /// <c>treatment_foods.carb_intake_id</c>. Reading candidates through the legacy treatment
    /// projection produced either a bolus id or, once a never-populated <c>DbId</c> gated the
    /// result, no suggestions at all.
    /// </summary>
    [Fact]
    public async Task GetSuggestionsAsync_ReturnsTheMatchedCarbIntakeId()
    {
        var carbIntakeId = Guid.CreateVersion7();
        _carbIntakeRepository
            .Setup(s => s.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new CarbIntake
            {
                Id = carbIntakeId,
                Carbs = 30,
                Timestamp = ConsumedAt.UtcDateTime,
            }]);

        _foodEntryRepository
            .Setup(r => r.GetPendingInTimeRangeAsync(
                It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Entry(Guid.NewGuid())]);

        var suggestions = await NewService().GetSuggestionsAsync(
            ConsumedAt.AddHours(-1), ConsumedAt.AddHours(1));

        suggestions.Should().ContainSingle()
            .Which.CarbIntakeId.Should().Be(carbIntakeId);
    }

    /// <summary>
    /// A window holding more candidates than the fetch limit is truncated by the database, so
    /// the fetch has to be newest-first — the pending entries being matched are the recent
    /// ones, and oldest-first spends the whole budget on the far end of the range.
    /// </summary>
    [Fact]
    public async Task GetSuggestionsAsync_FetchesTheNewestCandidatesWhenTheWindowIsTruncated()
    {
        _foodEntryRepository
            .Setup(r => r.GetPendingInTimeRangeAsync(
                It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Entry(Guid.NewGuid())]);

        await NewService().GetSuggestionsAsync(ConsumedAt.AddMonths(-6), ConsumedAt);

        _carbIntakeRepository.Verify(
            s => s.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), true, It.IsAny<bool>(),
                It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    /// <summary>
    /// Npgsql rejects a DateTimeOffset carrying any offset but zero against a
    /// "timestamp with time zone", and binding a bare date off the query string gives one the
    /// server's own offset. Unnormalised, the whole Meals page 500s on any host that is not on
    /// UTC, which is every developer machine and no production one.
    /// </summary>
    [Fact]
    public async Task GetSuggestionsAsync_QueriesInUtcWhateverOffsetItIsHanded()
    {
        var captured = new List<DateTimeOffset>();
        _foodEntryRepository
            .Setup(r => r.GetPendingInTimeRangeAsync(
                It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Callback((DateTimeOffset from, DateTimeOffset to, CancellationToken _) =>
            {
                captured.Add(from);
                captured.Add(to);
            })
            .ReturnsAsync([]);

        var offset = TimeSpan.FromHours(10);
        var from = new DateTimeOffset(2026, 9, 8, 0, 0, 0, offset);

        await NewService().GetSuggestionsAsync(from, from.AddDays(1));

        captured.Should().OnlyContain(instant => instant.Offset == TimeSpan.Zero);
        captured[0].Should().Be(from);
    }

    /// <summary>
    /// <c>treatment_foods.carb_intake_id</c> has no foreign key, so an unknown id would persist
    /// a row nothing can resolve and strand the food entry in Matched.
    /// </summary>
    [Fact]
    public async Task AcceptMatchAsync_WritesNothingWhenTheCarbIntakeDoesNotExist()
    {
        var foodEntryId = Guid.NewGuid();
        _foodEntryRepository
            .Setup(r => r.GetByIdAsync(foodEntryId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Entry(foodEntryId));

        _carbIntakeRepository
            .Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CarbIntake?)null);

        await NewService().AcceptMatchAsync(foodEntryId, Guid.CreateVersion7(), 30, 0);

        _treatmentFoodService.Verify(
            s => s.AddAsync(It.IsAny<TreatmentFood>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _foodEntryRepository.Verify(
            r => r.UpdateStatusAsync(
                It.IsAny<Guid>(), It.IsAny<ConnectorFoodEntryStatus>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
