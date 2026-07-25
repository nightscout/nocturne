using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Configuration;
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
    private readonly Mock<ITreatmentStore> _treatmentStore = new();
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

        // Every entry has a treatment it matches, so each one wants to raise a notification.
        _treatmentStore
            .Setup(s => s.QueryAsync(It.IsAny<TreatmentQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new Treatment
            {
                Id = Guid.NewGuid().ToString(),
                Carbs = 30,
                Mills = ConsumedAt.ToUnixTimeMilliseconds(),
            }]);
    }

    private MealMatchingService NewService() =>
        new(_foodEntryRepository.Object,
            _treatmentStore.Object,
            _treatmentFoodService.Object,
            _notificationService.Object,
            _notificationRepository.Object,
            _settingsService.Object,
            Mock.Of<ILogger<MealMatchingService>>());

    private static ConnectorFoodEntry Entry(Guid id) => new()
    {
        Id = id,
        ConnectorSource = "myfitnesspal-connector",
        MealName = "Breakfast",
        Carbs = 30,
        ConsumedAt = ConsumedAt,
        Status = ConnectorFoodEntryStatus.Pending,
    };

    [Fact]
    public async Task ProcessNewFoodEntriesAsync_KeepsGoingWhenOneEntryCannotBeNotified()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        _foodEntryRepository
            .Setup(r => r.GetByIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ids.Select(Entry).ToList());

        // The notification source's active cap throws; before this was guarded it abandoned the
        // rest of the batch, so matching silently stopped partway through.
        _notificationService
            .Setup(n => n.CreateNotificationAsync(
                UserId, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<NotificationCategory?>(),
                It.IsAny<NotificationUrgency?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), ids[0].ToString(), It.IsAny<List<NotificationActionDto>?>(),
                It.IsAny<ResolutionConditions?>(), It.IsAny<Dictionary<string, object>?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Rate limit exceeded"));

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
}
