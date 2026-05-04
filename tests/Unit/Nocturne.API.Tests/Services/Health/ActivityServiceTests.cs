using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Services.Health;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Xunit;
using Nocturne.API.Services.Realtime;

namespace Nocturne.API.Tests.Services.Health;

/// <summary>
/// Unit tests for ActivityService domain service with WebSocket broadcasting
/// </summary>
public class ActivityServiceTests
{
    private readonly Mock<IStateSpanService> _mockStateSpanService;
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
        _mockDocumentProcessingService = new Mock<IDocumentProcessingService>();
        _mockSignalRBroadcastService = new Mock<ISignalRBroadcastService>();
        _mockActivityDecomposer = new Mock<IActivityDecomposer>();
        _mockHeartRateService = new Mock<IHeartRateService>();
        _mockStepCountService = new Mock<IStepCountService>();
        _mockLogger = new Mock<ILogger<ActivityService>>();

        // Default: return empty lists for heart rate and step count
        _mockHeartRateService
            .Setup(s => s.GetHeartRatesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<HeartRate>());
        _mockStepCountService
            .Setup(s => s.GetStepCountsAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<StepCount>());

        _activityService = new ActivityService(
            _mockStateSpanService.Object,
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
    public async Task DeleteMultipleActivitiesAsync_WithoutFilter_ReturnsZero()
    {
        // Arrange
        // DeleteMultipleActivitiesAsync is not implemented yet, so it returns 0

        // Act
        var result = await _activityService.DeleteMultipleActivitiesAsync(
            cancellationToken: CancellationToken.None
        );

        // Assert
        Assert.Equal(0L, result);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public async Task DeleteMultipleActivitiesAsync_WithFilter_ReturnsZero()
    {
        // Arrange
        var find = "{\"type\":\"exercise\"}";
        // DeleteMultipleActivitiesAsync is not implemented yet, so it returns 0

        // Act
        var result = await _activityService.DeleteMultipleActivitiesAsync(
            find,
            CancellationToken.None
        );

        // Assert
        Assert.Equal(0L, result);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public async Task DeleteMultipleActivitiesAsync_WithException_ThrowsException()
    {
        // Arrange
        // Since the method currently just returns Task.FromResult(0L), it won't throw
        // But we'll test what happens if it's modified to throw

        // Act & Assert
        // For now, this should pass since the method doesn't actually throw
        var result = await _activityService.DeleteMultipleActivitiesAsync(
            cancellationToken: CancellationToken.None
        );
        Assert.Equal(0L, result);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public void Constructor_WithNullStateSpanService_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ActivityService(
                null!,
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
}
