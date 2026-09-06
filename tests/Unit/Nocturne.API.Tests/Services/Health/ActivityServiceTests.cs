using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Authorization;
using Nocturne.API.Services.Health;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Sleep;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Xunit;
using Nocturne.API.Services.Realtime;

namespace Nocturne.API.Tests.Services.Health;

/// <summary>
/// Unit tests for ActivityService domain service with WebSocket broadcasting
/// </summary>
public class ActivityServiceTests
{
    private readonly Mock<IStateSpanService> _mockStateSpanService;
    private readonly Mock<ISleepService> _mockSleepService;
    private readonly Mock<IDocumentProcessingService> _mockDocumentProcessingService;
    private readonly Mock<ISignalRBroadcastService> _mockSignalRBroadcastService;
    private readonly Mock<IActivityDecomposer> _mockActivityDecomposer;
    private readonly Mock<IHeartRateService> _mockHeartRateService;
    private readonly Mock<IStepCountService> _mockStepCountService;
    private readonly Mock<ILogger<ActivityService>> _mockLogger;
    private readonly ActivityService _activityService;

    public ActivityServiceTests()
    {
        _mockStateSpanService = new Mock<IStateSpanService>();
        _mockSleepService = new Mock<ISleepService>();
        _mockDocumentProcessingService = new Mock<IDocumentProcessingService>();
        _mockSignalRBroadcastService = new Mock<ISignalRBroadcastService>();
        _mockActivityDecomposer = new Mock<IActivityDecomposer>();
        _mockHeartRateService = new Mock<IHeartRateService>();
        _mockStepCountService = new Mock<IStepCountService>();
        _mockLogger = new Mock<ILogger<ActivityService>>();

        // Default: return empty lists for heart rate, step count, and sleep
        _mockHeartRateService
            .Setup(s => s.GetHeartRatesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<HeartRate>());
        _mockStepCountService
            .Setup(s => s.GetStepCountsAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<StepCount>());
        _mockSleepService
            .Setup(s => s.GetSessionsAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<SleepSessionType?>(),
                It.IsAny<SleepSource?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<SleepSession>());
        _mockSleepService
            .Setup(s => s.CountSessionsAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<SleepSessionType?>(),
                It.IsAny<SleepSource?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        _activityService = new ActivityService(
            _mockStateSpanService.Object,
            _mockSleepService.Object,
            _mockDocumentProcessingService.Object,
            _mockSignalRBroadcastService.Object,
            Mock.Of<IDataEventSink<Activity>>(),
            _mockActivityDecomposer.Object,
            _mockHeartRateService.Object,
            _mockStepCountService.Object,
            _mockLogger.Object
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public async Task GetActivitiesAsync_WithoutParameters_ReturnsAllActivities()
    {
        // Arrange
        var expectedActivities = new List<Activity>
        {
            new Activity
            {
                Id = "1",
                Type = "exercise",
                Description = "Running",
                Duration = 30,
                Mills = 1234567890,
            },
            new Activity
            {
                Id = "2",
                Type = "meal",
                Description = "Breakfast",
                Duration = 15,
                Mills = 1234567880,
            },
        };

        _mockStateSpanService
            .Setup(x =>
                x.GetActivitiesAsync(It.IsAny<string?>(), 10, 0, It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(expectedActivities);

        // Act
        var result = await _activityService.GetActivitiesAsync(
            cancellationToken: CancellationToken.None
        );

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Count());
        Assert.Equal(expectedActivities, result);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public async Task GetActivitiesAsync_WithParameters_ReturnsFilteredActivities()
    {
        // Arrange
        var find = "{\"type\":\"exercise\"}";
        var count = 5;
        var skip = 0;
        var expectedActivities = new List<Activity>
        {
            new Activity
            {
                Id = "1",
                Type = "exercise",
                Description = "Running",
                Duration = 30,
                Mills = 1234567890,
            },
        };

        _mockStateSpanService
            .Setup(x =>
                x.GetActivitiesAsync(
                    It.IsAny<string?>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(expectedActivities);

        // Act
        var result = await _activityService.GetActivitiesAsync(
            find,
            count,
            skip,
            CancellationToken.None
        );

        // Assert
        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal(expectedActivities.First().Id, result.First().Id);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public async Task GetActivitiesAsync_WithException_ThrowsException()
    {
        // Arrange
        _mockStateSpanService
            .Setup(x =>
                x.GetActivitiesAsync(
                    It.IsAny<string?>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ThrowsAsync(new Exception("Database error"));

        // Act & Assert
        await Assert.ThrowsAsync<Exception>(() =>
            _activityService.GetActivitiesAsync(cancellationToken: CancellationToken.None)
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public async Task GetActivityByIdAsync_WithValidId_ReturnsActivity()
    {
        // Arrange
        var activityId = "60a1b2c3d4e5f6789012345";
        var expectedActivity = new Activity
        {
            Id = activityId,
            Type = "exercise",
            Description = "Running",
            Duration = 30,
            Mills = 1234567890,
        };

        _mockStateSpanService
            .Setup(x => x.GetActivityByIdAsync(activityId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedActivity);

        // Act
        var result = await _activityService.GetActivityByIdAsync(
            activityId,
            CancellationToken.None
        );

        // Assert
        Assert.NotNull(result);
        Assert.Equal(activityId, result.Id);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public async Task GetActivityByIdAsync_WithInvalidId_ReturnsNull()
    {
        // Arrange
        var activityId = "invalidid";

        _mockStateSpanService
            .Setup(x => x.GetActivityByIdAsync(activityId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Activity?)null);

        // Act
        var result = await _activityService.GetActivityByIdAsync(
            activityId,
            CancellationToken.None
        );

        // Assert
        Assert.Null(result);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public async Task GetActivityByIdAsync_WithException_ThrowsException()
    {
        // Arrange
        var activityId = "test-id";

        _mockStateSpanService
            .Setup(x => x.GetActivityByIdAsync(activityId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Database error"));

        // Act & Assert
        await Assert.ThrowsAsync<Exception>(() =>
            _activityService.GetActivityByIdAsync(activityId, CancellationToken.None)
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public async Task CreateActivitiesAsync_WithValidActivities_ReturnsCreatedActivitiesAndBroadcasts()
    {
        // Arrange
        var activities = new List<Activity>
        {
            new Activity
            {
                Type = "exercise",
                Description = "Running",
                Duration = 30,
                Mills = 1234567890,
            },
            new Activity
            {
                Type = "meal",
                Description = "Breakfast",
                Duration = 15,
                Mills = 1234567880,
            },
        };

        var processedActivities = activities
            .Select(a => new Activity
            {
                Id = Guid.NewGuid().ToString(),
                Type = a.Type,
                Description = a.Description,
                Duration = a.Duration,
                Mills = a.Mills,
            })
            .ToList();

        var createdActivities = processedActivities.ToList();

        _mockDocumentProcessingService
            .Setup(x => x.ProcessDocuments(It.IsAny<IEnumerable<Activity>>()))
            .Returns(processedActivities);

        _mockStateSpanService
            .Setup(x =>
                x.CreateActivitiesAsync(
                    It.IsAny<IEnumerable<Activity>>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(createdActivities);

        // Act
        var result = await _activityService.CreateActivitiesAsync(
            activities,
            CancellationToken.None
        );

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Count());
        _mockDocumentProcessingService.Verify(
            x => x.ProcessDocuments(It.IsAny<IEnumerable<Activity>>()),
            Times.Once
        );
        _mockStateSpanService.Verify(
            x =>
                x.CreateActivitiesAsync(
                    It.IsAny<IEnumerable<Activity>>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
        _mockSignalRBroadcastService.Verify(
            x => x.BroadcastStorageCreateAsync("activity", It.IsAny<object>()),
            Times.Once
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public async Task CreateActivitiesAsync_WithException_ThrowsException()
    {
        // Arrange
        var activities = new List<Activity>
        {
            new Activity
            {
                Type = "exercise",
                Description = "Running",
                Duration = 30,
                Mills = 1234567890,
            },
        };

        _mockDocumentProcessingService
            .Setup(x => x.ProcessDocuments(It.IsAny<IEnumerable<Activity>>()))
            .Throws(new Exception("Processing error"));

        // Act & Assert
        await Assert.ThrowsAsync<Exception>(() =>
            _activityService.CreateActivitiesAsync(activities, CancellationToken.None)
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public async Task UpdateActivityAsync_WithValidActivity_ReturnsUpdatedActivityAndBroadcasts()
    {
        // Arrange
        var activityId = "60a1b2c3d4e5f6789012345";
        var activity = new Activity
        {
            Id = activityId,
            Type = "exercise",
            Description = "Running",
            Duration = 30,
            Mills = 1234567890,
        };
        var updatedActivity = new Activity
        {
            Id = activityId,
            Type = "exercise",
            Description = "Jogging",
            Duration = 45,
            Mills = 1234567890,
        };

        _mockStateSpanService
            .Setup(x => x.UpdateActivityAsync(activityId, activity, It.IsAny<CancellationToken>()))
            .ReturnsAsync(updatedActivity);

        // Act
        var result = await _activityService.UpdateActivityAsync(
            activityId,
            activity,
            CancellationToken.None
        );

        // Assert
        Assert.NotNull(result);
        Assert.Equal(activityId, result.Id);
        Assert.Equal("Jogging", result.Description);
        Assert.Equal(45, result.Duration);
        _mockStateSpanService.Verify(
            x => x.UpdateActivityAsync(activityId, activity, It.IsAny<CancellationToken>()),
            Times.Once
        );
        _mockSignalRBroadcastService.Verify(
            x => x.BroadcastStorageUpdateAsync("activity", It.IsAny<object>()),
            Times.Once
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public async Task UpdateActivityAsync_WithInvalidId_ReturnsNullAndDoesNotBroadcast()
    {
        // Arrange
        var activityId = "invalidid";
        var activity = new Activity
        {
            Type = "exercise",
            Description = "Running",
            Duration = 30,
            Mills = 1234567890,
        };

        _mockStateSpanService
            .Setup(x => x.UpdateActivityAsync(activityId, activity, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Activity?)null);

        // Act
        var result = await _activityService.UpdateActivityAsync(
            activityId,
            activity,
            CancellationToken.None
        );

        // Assert
        Assert.Null(result);
        _mockStateSpanService.Verify(
            x => x.UpdateActivityAsync(activityId, activity, It.IsAny<CancellationToken>()),
            Times.Once
        );
        _mockSignalRBroadcastService.Verify(
            x => x.BroadcastStorageUpdateAsync("activity", It.IsAny<object>()),
            Times.Never
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public async Task UpdateActivityAsync_WithException_ThrowsException()
    {
        // Arrange
        var activityId = "test-id";
        var activity = new Activity
        {
            Type = "exercise",
            Description = "Running",
            Duration = 30,
            Mills = 1234567890,
        };

        _mockStateSpanService
            .Setup(x => x.UpdateActivityAsync(activityId, activity, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Database error"));

        // Act & Assert
        await Assert.ThrowsAsync<Exception>(() =>
            _activityService.UpdateActivityAsync(activityId, activity, CancellationToken.None)
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public async Task DeleteActivityAsync_WithValidId_ReturnsTrueAndBroadcasts()
    {
        // Arrange
        var activityId = "60a1b2c3d4e5f6789012345";

        _mockStateSpanService
            .Setup(x => x.DeleteActivityAsync(activityId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        var result = await _activityService.DeleteActivityAsync(activityId, CancellationToken.None);

        // Assert
        Assert.True(result);
        _mockStateSpanService.Verify(
            x => x.DeleteActivityAsync(activityId, It.IsAny<CancellationToken>()),
            Times.Once
        );
        _mockSignalRBroadcastService.Verify(
            x => x.BroadcastStorageDeleteAsync("activity", It.IsAny<object>()),
            Times.Once
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public async Task DeleteActivityAsync_WithInvalidId_ReturnsFalseAndDoesNotBroadcast()
    {
        // Arrange
        var activityId = "invalidid";

        _mockStateSpanService
            .Setup(x => x.DeleteActivityAsync(activityId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Act
        var result = await _activityService.DeleteActivityAsync(activityId, CancellationToken.None);

        // Assert
        Assert.False(result);
        _mockStateSpanService.Verify(
            x => x.DeleteActivityAsync(activityId, It.IsAny<CancellationToken>()),
            Times.Once
        );
        _mockSignalRBroadcastService.Verify(
            x => x.BroadcastStorageDeleteAsync("activity", It.IsAny<object>()),
            Times.Never
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public async Task DeleteActivityAsync_WithException_ThrowsException()
    {
        // Arrange
        var activityId = "test-id";

        _mockStateSpanService
            .Setup(x => x.DeleteActivityAsync(activityId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Database error"));

        // Act & Assert
        await Assert.ThrowsAsync<Exception>(() =>
            _activityService.DeleteActivityAsync(activityId, CancellationToken.None)
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public async Task DeleteMultipleActivitiesAsync_WithoutFilter_DeletesAllStateSpanActivities()
    {
        // Arrange
        var activities = new List<Activity>
        {
            new Activity { Id = "1", Type = "exercise", Mills = 1 },
            new Activity { Id = "2", Type = "meal", Mills = 2 },
        };
        _mockStateSpanService
            .Setup(s => s.GetActivitiesAsync(null, int.MaxValue, 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(activities);
        _mockStateSpanService
            .Setup(s => s.DeleteActivityAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        var result = await _activityService.DeleteMultipleActivitiesAsync(
            cancellationToken: CancellationToken.None
        );

        // Assert
        Assert.Equal(2L, result);
        _mockStateSpanService.Verify(
            s => s.DeleteActivityAsync("1", It.IsAny<CancellationToken>()),
            Times.Once
        );
        _mockStateSpanService.Verify(
            s => s.DeleteActivityAsync("2", It.IsAny<CancellationToken>()),
            Times.Once
        );
        _mockActivityDecomposer.Verify(
            d => d.DeleteByLegacyIdAsync(It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2)
        );
        _mockSignalRBroadcastService.Verify(
            s => s.BroadcastStorageDeleteAsync("activity", It.IsAny<object>()),
            Times.Once
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public async Task DeleteMultipleActivitiesAsync_WithFilter_PassesFilterAndCountsDeleted()
    {
        // Arrange
        var find = "{\"type\":\"exercise\"}";
        var activities = new List<Activity>
        {
            new Activity { Id = "1", Type = "exercise", Mills = 1 },
        };
        _mockStateSpanService
            .Setup(s => s.GetActivitiesAsync(find, int.MaxValue, 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(activities);
        _mockStateSpanService
            .Setup(s => s.DeleteActivityAsync("1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        var result = await _activityService.DeleteMultipleActivitiesAsync(
            find,
            CancellationToken.None
        );

        // Assert
        Assert.Equal(1L, result);
        _mockStateSpanService.Verify(
            s => s.GetActivitiesAsync(find, int.MaxValue, 0, It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public async Task DeleteMultipleActivitiesAsync_WithNoMatches_ReturnsZeroAndDoesNotBroadcast()
    {
        // Arrange
        _mockStateSpanService
            .Setup(s => s.GetActivitiesAsync(null, int.MaxValue, 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<Activity>());

        // Act
        var result = await _activityService.DeleteMultipleActivitiesAsync(
            cancellationToken: CancellationToken.None
        );

        // Assert
        Assert.Equal(0L, result);
        _mockSignalRBroadcastService.Verify(
            s => s.BroadcastStorageDeleteAsync(It.IsAny<string>(), It.IsAny<object>()),
            Times.Never
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public async Task DeleteMultipleActivitiesAsync_WithException_ThrowsException()
    {
        // Arrange
        _mockStateSpanService
            .Setup(s => s.GetActivitiesAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Database error"));

        // Act & Assert
        await Assert.ThrowsAsync<Exception>(() =>
            _activityService.DeleteMultipleActivitiesAsync(cancellationToken: CancellationToken.None)
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public void Constructor_WithNullStateSpanService_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ActivityService(
                null!,
                _mockSleepService.Object,
                _mockDocumentProcessingService.Object,
                _mockSignalRBroadcastService.Object,
                Mock.Of<IDataEventSink<Activity>>(),
                _mockActivityDecomposer.Object,
                _mockHeartRateService.Object,
                _mockStepCountService.Object,
                _mockLogger.Object
            )
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public void Constructor_WithNullDocumentProcessingService_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ActivityService(
                _mockStateSpanService.Object,
                _mockSleepService.Object,
                null!,
                _mockSignalRBroadcastService.Object,
                Mock.Of<IDataEventSink<Activity>>(),
                _mockActivityDecomposer.Object,
                _mockHeartRateService.Object,
                _mockStepCountService.Object,
                _mockLogger.Object
            )
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public void Constructor_WithNullSignalRBroadcastService_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ActivityService(
                _mockStateSpanService.Object,
                _mockSleepService.Object,
                _mockDocumentProcessingService.Object,
                null!,
                Mock.Of<IDataEventSink<Activity>>(),
                _mockActivityDecomposer.Object,
                _mockHeartRateService.Object,
                _mockStepCountService.Object,
                _mockLogger.Object
            )
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Constructor_WithNullActivityDecomposer_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ActivityService(
                _mockStateSpanService.Object,
                _mockSleepService.Object,
                _mockDocumentProcessingService.Object,
                _mockSignalRBroadcastService.Object,
                Mock.Of<IDataEventSink<Activity>>(),
                null!,
                _mockHeartRateService.Object,
                _mockStepCountService.Object,
                _mockLogger.Object
            )
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Constructor_WithNullHeartRateService_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ActivityService(
                _mockStateSpanService.Object,
                _mockSleepService.Object,
                _mockDocumentProcessingService.Object,
                _mockSignalRBroadcastService.Object,
                Mock.Of<IDataEventSink<Activity>>(),
                _mockActivityDecomposer.Object,
                null!,
                _mockStepCountService.Object,
                _mockLogger.Object
            )
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Constructor_WithNullStepCountService_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ActivityService(
                _mockStateSpanService.Object,
                _mockSleepService.Object,
                _mockDocumentProcessingService.Object,
                _mockSignalRBroadcastService.Object,
                Mock.Of<IDataEventSink<Activity>>(),
                _mockActivityDecomposer.Object,
                _mockHeartRateService.Object,
                null!,
                _mockLogger.Object
            )
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public void Constructor_WithNullLogger_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ActivityService(
                _mockStateSpanService.Object,
                _mockSleepService.Object,
                _mockDocumentProcessingService.Object,
                _mockSignalRBroadcastService.Object,
                Mock.Of<IDataEventSink<Activity>>(),
                _mockActivityDecomposer.Object,
                _mockHeartRateService.Object,
                _mockStepCountService.Object,
                null!
            )
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CountActivitiesAsync_SumsAllDecomposedSources()
    {
        // Arrange
        var stateSpanActivities = new List<Activity>
        {
            new() { Id = "1", Mills = 1000 },
            new() { Id = "2", Mills = 2000 },
        };
        var heartRates = new List<HeartRate>
        {
            new() { Id = Guid.NewGuid().ToString() },
        };
        var stepCounts = new List<StepCount>
        {
            new() { Id = Guid.NewGuid().ToString() },
            new() { Id = Guid.NewGuid().ToString() },
            new() { Id = Guid.NewGuid().ToString() },
        };

        _mockStateSpanService
            .Setup(s => s.GetActivitiesAsync(
                It.IsAny<string?>(), int.MaxValue, 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(stateSpanActivities);
        _mockHeartRateService
            .Setup(s => s.GetHeartRatesAsync(int.MaxValue, 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(heartRates);
        _mockStepCountService
            .Setup(s => s.GetStepCountsAsync(int.MaxValue, 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(stepCounts);

        // Act
        var count = await _activityService.CountActivitiesAsync(cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(6, count);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CountActivitiesByCategoryAsync_KeysEachSourceByItsReadScope()
    {
        _mockStateSpanService
            .Setup(s => s.GetActivitiesAsync(
                It.IsAny<string?>(), int.MaxValue, 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new Activity { Id = "1", Mills = 1000 }]);
        _mockHeartRateService
            .Setup(s => s.GetHeartRatesAsync(int.MaxValue, 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new HeartRate(), new HeartRate()]);
        _mockStepCountService
            .Setup(s => s.GetStepCountsAsync(int.MaxValue, 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new StepCount(), new StepCount(), new StepCount()]);
        _mockSleepService
            .Setup(s => s.CountSessionsAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<SleepSessionType?>(),
                It.IsAny<SleepSource?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(4);

        var counts = await _activityService.CountActivitiesByCategoryAsync(
            AllCategories, cancellationToken: CancellationToken.None);

        counts.Should().BeEquivalentTo(new Dictionary<string, long>
        {
            [Scope.TreatmentsRead] = 1,
            [Scope.HeartRateRead] = 2,
            [Scope.StepCountRead] = 3,
            [Scope.SleepRead] = 4,
        });
    }

    /// <summary>
    /// Each source is materialized in full to be counted, so a category nobody asked for must not
    /// be fetched at all.
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task CountActivitiesByCategoryAsync_DoesNotQueryASourceItWasNotAskedFor()
    {
        _mockHeartRateService
            .Setup(s => s.GetHeartRatesAsync(int.MaxValue, 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new HeartRate(), new HeartRate()]);

        var counts = await _activityService.CountActivitiesByCategoryAsync(
            new HashSet<string> { Scope.HeartRateRead },
            cancellationToken: CancellationToken.None);

        counts.Should().BeEquivalentTo(new Dictionary<string, long>
        {
            [Scope.HeartRateRead] = 2,
        });
        _mockStateSpanService.Verify(
            s => s.GetActivitiesAsync(
                It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _mockStepCountService.Verify(
            s => s.GetStepCountsAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _mockSleepService.Verify(
            s => s.CountSessionsAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<SleepSessionType?>(),
                It.IsAny<SleepSource?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// The categories the merged read endpoints ask for. Driving the count from the guard's own
    /// list is what keeps a category the endpoints admit from silently going uncounted.
    /// </summary>
    private static readonly IReadOnlySet<string> AllCategories =
        ActivityReadScopeGuard.AdmissionScopes.ToHashSet();

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CountActivitiesAsync_WithEmptySources_ReturnsZero()
    {
        // Arrange
        _mockStateSpanService
            .Setup(s => s.GetActivitiesAsync(
                It.IsAny<string?>(), int.MaxValue, 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<Activity>());
        _mockHeartRateService
            .Setup(s => s.GetHeartRatesAsync(int.MaxValue, 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<HeartRate>());
        _mockStepCountService
            .Setup(s => s.GetStepCountsAsync(int.MaxValue, 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<StepCount>());

        // Act
        var count = await _activityService.CountActivitiesAsync(cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(0, count);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CreateActivitiesAsync_SleepActivity_UpsertsSessionWithOriginalId()
    {
        // Arrange
        var activity = new Activity
        {
            Id = "abc123",
            Type = "sleep",
            Duration = 480,
            Mills = 1234567890000,
        };

        _mockDocumentProcessingService
            .Setup(x => x.ProcessDocuments(It.IsAny<IEnumerable<Activity>>()))
            .Returns(new List<Activity> { activity });

        SleepSession? upsertedSession = null;
        _mockSleepService
            .Setup(s => s.UpsertSessionAsync(It.IsAny<SleepSession>(), It.IsAny<CancellationToken>()))
            .Callback<SleepSession, CancellationToken>((s, _) => upsertedSession = s)
            .ReturnsAsync((SleepSession s, CancellationToken _) => s);

        // Act
        var result = await _activityService.CreateActivitiesAsync(
            new[] { activity },
            CancellationToken.None
        );

        // Assert: the session carries the activity id as OriginalId so the repository
        // dedup (Source + OriginalId) matches a re-POST of the same record
        Assert.NotNull(upsertedSession);
        Assert.Equal("abc123", upsertedSession.OriginalId);
        Assert.Equal("abc123", result.Single().Id);
        _mockStateSpanService.Verify(
            x => x.CreateActivitiesAsync(It.IsAny<IEnumerable<Activity>>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("sleep")]
    [InlineData("nap")]
    [InlineData("Sleep")]
    public async Task CreateActivitiesAsync_ExactSleepType_RoutesToSleepService(string type)
    {
        // Arrange
        var activity = new Activity
        {
            Id = "sleep-1",
            Type = type,
            Duration = 480,
            Mills = 1234567890000,
        };

        _mockDocumentProcessingService
            .Setup(x => x.ProcessDocuments(It.IsAny<IEnumerable<Activity>>()))
            .Returns(new List<Activity> { activity });
        _mockSleepService
            .Setup(s => s.UpsertSessionAsync(It.IsAny<SleepSession>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SleepSession s, CancellationToken _) => s);

        // Act
        await _activityService.CreateActivitiesAsync(new[] { activity }, CancellationToken.None);

        // Assert
        _mockSleepService.Verify(
            s => s.UpsertSessionAsync(It.IsAny<SleepSession>(), It.IsAny<CancellationToken>()),
            Times.Once
        );
        _mockStateSpanService.Verify(
            x => x.CreateActivitiesAsync(It.IsAny<IEnumerable<Activity>>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("restaurant")]
    [InlineData("rest day")]
    [InlineData("snap")]
    public async Task CreateActivitiesAsync_TypeContainingSleepWord_NotRoutedToSleep(string type)
    {
        // Arrange
        var activity = new Activity
        {
            Id = "act-1",
            Type = type,
            Duration = 60,
            Mills = 1234567890000,
        };

        _mockDocumentProcessingService
            .Setup(x => x.ProcessDocuments(It.IsAny<IEnumerable<Activity>>()))
            .Returns(new List<Activity> { activity });
        _mockStateSpanService
            .Setup(x =>
                x.CreateActivitiesAsync(
                    It.IsAny<IEnumerable<Activity>>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new List<Activity> { activity });

        // Act
        await _activityService.CreateActivitiesAsync(new[] { activity }, CancellationToken.None);

        // Assert
        _mockSleepService.Verify(
            s => s.UpsertSessionAsync(It.IsAny<SleepSession>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
        _mockStateSpanService.Verify(
            x => x.CreateActivitiesAsync(It.IsAny<IEnumerable<Activity>>(), It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task UpdateActivityAsync_IdResolvesToSleepSession_RoutesToSleepService()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var existingSession = new SleepSession
        {
            Id = sessionId.ToString(),
            Source = SleepSource.Manual,
            OriginalId = "abc123",
            StartTime = DateTime.UtcNow.AddHours(-8),
            EndTime = DateTime.UtcNow,
        };
        var activity = new Activity
        {
            Id = sessionId.ToString(),
            Type = "sleep",
            Duration = 420,
            Mills = 1234567890000,
        };

        _mockSleepService
            .Setup(s => s.GetSessionByIdAsync(sessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingSession);

        SleepSession? updatedArg = null;
        _mockSleepService
            .Setup(s => s.UpdateSessionAsync(sessionId, It.IsAny<SleepSession>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, SleepSession, CancellationToken>((_, s, _) => updatedArg = s)
            .ReturnsAsync((Guid _, SleepSession s, CancellationToken _) => s);

        // Act
        var result = await _activityService.UpdateActivityAsync(
            sessionId.ToString(),
            activity,
            CancellationToken.None
        );

        // Assert
        Assert.NotNull(result);
        Assert.NotNull(updatedArg);
        // The stored row's dedup key is preserved, not replaced by the session Guid from the payload
        Assert.Equal("abc123", updatedArg.OriginalId);
        _mockStateSpanService.Verify(
            x => x.UpdateActivityAsync(It.IsAny<string>(), It.IsAny<Activity>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
        _mockSignalRBroadcastService.Verify(
            x => x.BroadcastStorageUpdateAsync("activity", It.IsAny<object>()),
            Times.Once
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task UpdateActivityAsync_SleepTypeWithNonGuidId_UpsertsByOriginalId()
    {
        // Arrange
        var activityId = "60a1b2c3d4e5f6789012345";
        var activity = new Activity
        {
            Id = activityId,
            Type = "sleep",
            Duration = 480,
            Mills = 1234567890000,
        };

        SleepSession? upsertedSession = null;
        _mockSleepService
            .Setup(s => s.UpsertSessionAsync(It.IsAny<SleepSession>(), It.IsAny<CancellationToken>()))
            .Callback<SleepSession, CancellationToken>((s, _) => upsertedSession = s)
            .ReturnsAsync((SleepSession s, CancellationToken _) => s);

        // Act
        var result = await _activityService.UpdateActivityAsync(
            activityId,
            activity,
            CancellationToken.None
        );

        // Assert
        Assert.NotNull(result);
        Assert.NotNull(upsertedSession);
        Assert.Equal(activityId, upsertedSession.OriginalId);
        _mockStateSpanService.Verify(
            x => x.UpdateActivityAsync(It.IsAny<string>(), It.IsAny<Activity>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
        _mockSignalRBroadcastService.Verify(
            x => x.BroadcastStorageUpdateAsync("activity", It.IsAny<object>()),
            Times.Once
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CountActivitiesAsync_WithNonSleepFind_ExcludesSleepSessions()
    {
        // Arrange
        _mockStateSpanService
            .Setup(s => s.GetActivitiesAsync(
                It.IsAny<string?>(), int.MaxValue, 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Activity>
            {
                new() { Id = "1", Mills = 1000 },
                new() { Id = "2", Mills = 2000 },
            });
        _mockSleepService
            .Setup(s => s.CountSessionsAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<SleepSessionType?>(),
                It.IsAny<SleepSource?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(5);

        // Act
        var count = await _activityService.CountActivitiesAsync("exercise", CancellationToken.None);

        // Assert: GetActivitiesAsync only merges sleep when `find` is empty or a
        // sleep type; the count applies the same gate
        Assert.Equal(2, count);
        _mockSleepService.Verify(
            s => s.CountSessionsAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<SleepSessionType?>(),
                It.IsAny<SleepSource?>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CountActivitiesAsync_WithSleepFind_IncludesSleepSessions()
    {
        // Arrange
        _mockStateSpanService
            .Setup(s => s.GetActivitiesAsync(
                It.IsAny<string?>(), int.MaxValue, 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<Activity>());
        _mockSleepService
            .Setup(s => s.CountSessionsAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<SleepSessionType?>(),
                It.IsAny<SleepSource?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(5);

        // Act
        var count = await _activityService.CountActivitiesAsync("sleep", CancellationToken.None);

        // Assert
        Assert.Equal(5, count);
    }

    /// <summary>
    /// The over-fetch is <c>count + skip</c>, which overflows int for a large skip and hands every
    /// source a negative fetch count. Clamping keeps it inside the service's own over-fetch bound;
    /// paging past the end of the merged set then yields nothing, which is correct. The expected
    /// value mirrors the service's private <c>MaxOverFetch</c>, which is deliberately not shared
    /// with the controller-level ceilings.
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetActivitiesAsync_SaturatesTheOverFetchInsteadOfOverflowing()
    {
        int observedFetchCount = 0;
        _mockHeartRateService
            .Setup(s => s.GetHeartRatesAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback((int count, int _, CancellationToken _) => observedFetchCount = count)
            .ReturnsAsync(Enumerable.Empty<HeartRate>());
        _mockStateSpanService
            .Setup(s => s.GetActivitiesAsync(
                It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<Activity>());

        var result = await _activityService.GetActivitiesAsync(
            count: 1, skip: int.MaxValue, cancellationToken: CancellationToken.None);

        observedFetchCount.Should().Be(100_000);
        result.Should().BeEmpty();
    }
}
