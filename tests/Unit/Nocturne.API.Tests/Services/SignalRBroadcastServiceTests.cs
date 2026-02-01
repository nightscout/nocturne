using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Hubs;
using Nocturne.API.Services;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Unit.Services;

/// <summary>
/// Unit tests for SignalR broadcast service to ensure WebSocket events are properly sent
/// Tests broadcasting functionality for all event types
/// </summary>
[Parity]
public class SignalRBroadcastServiceTests
{
    private readonly Mock<IHubContext<DataHub>> _mockDataHubContext;
    private readonly Mock<IHubContext<AlarmHub>> _mockAlarmHubContext;
    private readonly Mock<IHubContext<ConfigHub>> _mockConfigHubContext;
    private readonly Mock<ILogger<SignalRBroadcastService>> _mockLogger;
    private readonly Mock<IHubClients> _mockDataClients;
    private readonly Mock<IHubClients> _mockAlarmClients;
    private readonly Mock<IHubClients> _mockConfigClients;
    private readonly Mock<IClientProxy> _mockDataGroupProxy;
    private readonly Mock<IClientProxy> _mockAlarmGroupProxy;
    private readonly Mock<IClientProxy> _mockConfigGroupProxy;
    private readonly SignalRBroadcastService _service;

    public SignalRBroadcastServiceTests()
    {
        _mockDataHubContext = new Mock<IHubContext<DataHub>>();
        _mockAlarmHubContext = new Mock<IHubContext<AlarmHub>>();
        _mockConfigHubContext = new Mock<IHubContext<ConfigHub>>();
        _mockLogger = new Mock<ILogger<SignalRBroadcastService>>();
        _mockDataClients = new Mock<IHubClients>();
        _mockAlarmClients = new Mock<IHubClients>();
        _mockConfigClients = new Mock<IHubClients>();
        _mockDataGroupProxy = new Mock<IClientProxy>();
        _mockAlarmGroupProxy = new Mock<IClientProxy>();
        _mockConfigGroupProxy = new Mock<IClientProxy>();

        _mockDataHubContext.Setup(x => x.Clients).Returns(_mockDataClients.Object);
        _mockAlarmHubContext.Setup(x => x.Clients).Returns(_mockAlarmClients.Object);
        _mockConfigHubContext.Setup(x => x.Clients).Returns(_mockConfigClients.Object);
        _mockDataClients
            .Setup(x => x.Group(It.IsAny<string>()))
            .Returns(_mockDataGroupProxy.Object);
        _mockAlarmClients
            .Setup(x => x.Group(It.IsAny<string>()))
            .Returns(_mockAlarmGroupProxy.Object);
        _mockConfigClients
            .Setup(x => x.Group(It.IsAny<string>()))
            .Returns(_mockConfigGroupProxy.Object);

        _service = new SignalRBroadcastService(
            _mockDataHubContext.Object,
            _mockAlarmHubContext.Object,
            _mockConfigHubContext.Object,
            _mockLogger.Object
        );
    }

    [Fact]
    [Parity]
    public async Task BroadcastDataUpdateAsync_ShouldSendToAuthorizedGroup()
    {
        // Arrange
        var testData = new { test = "data" };

        // Act
        await _service.BroadcastDataUpdateAsync(testData);

        // Assert
        _mockDataClients.Verify(x => x.Group("authorized"), Times.Once);
        _mockDataGroupProxy.Verify(
            x =>
                x.SendCoreAsync(
                    "dataUpdate",
                    It.Is<object[]>(args => args[0] == testData),
                    default
                ),
            Times.Once
        );
    }

    [Fact]
    [Parity]
    public async Task BroadcastStorageCreateAsync_ShouldSendToCollectionGroup()
    {
        // Arrange
        var collectionName = "treatments";
        var data = new { colName = "treatments", doc = new { id = "test" } };

        // Act
        await _service.BroadcastStorageCreateAsync(collectionName, data);

        // Assert
        _mockDataClients.Verify(x => x.Group(collectionName), Times.Once);
        _mockDataGroupProxy.Verify(
            x => x.SendCoreAsync("create", It.Is<object[]>(args => args[0] == data), default),
            Times.Once
        );
    }

    [Fact]
    [Parity]
    public async Task BroadcastStorageUpdateAsync_ShouldSendToCollectionGroup()
    {
        // Arrange
        var collectionName = "entries";
        var data = new { colName = "entries", doc = new { id = "test" } };

        // Act
        await _service.BroadcastStorageUpdateAsync(collectionName, data);

        // Assert
        _mockDataClients.Verify(x => x.Group(collectionName), Times.Once);
        _mockDataGroupProxy.Verify(
            x => x.SendCoreAsync("update", It.Is<object[]>(args => args[0] == data), default),
            Times.Once
        );
    }

    [Fact]
    [Parity]
    public async Task BroadcastStorageDeleteAsync_ShouldSendToCollectionGroup()
    {
        // Arrange
        var collectionName = "devicestatus";
        var data = new { colName = "devicestatus", doc = new { id = "test" } };

        // Act
        await _service.BroadcastStorageDeleteAsync(collectionName, data);

        // Assert
        _mockDataClients.Verify(x => x.Group(collectionName), Times.Once);
        _mockDataGroupProxy.Verify(
            x => x.SendCoreAsync("delete", It.Is<object[]>(args => args[0] == data), default),
            Times.Once
        );
    }

    [Fact]
    [Parity]
    public async Task BroadcastNotificationAsync_ShouldSendToAlarmSubscribers()
    {
        // Arrange
        var notification = new NotificationBase
        {
            Title = "Test Notification",
            Message = "Test message",
            Level = 0,
        };

        // Act
        await _service.BroadcastNotificationAsync(notification);

        // Assert
        _mockAlarmClients.Verify(x => x.Group("alarm-subscribers"), Times.Once);
        _mockAlarmGroupProxy.Verify(
            x =>
                x.SendCoreAsync(
                    "notification",
                    It.Is<object[]>(args => args[0] == notification),
                    default
                ),
            Times.Once
        );
    }

    [Fact]
    [Parity]
    public async Task BroadcastAlarmAsync_ShouldSendToAlarmSubscribers()
    {
        // Arrange
        var alarm = new NotificationBase
        {
            Title = "Test Alarm",
            Message = "Test alarm message",
            Level = 1,
        };

        // Act
        await _service.BroadcastAlarmAsync(alarm);

        // Assert
        _mockAlarmClients.Verify(x => x.Group("alarm-subscribers"), Times.Once);
        _mockAlarmGroupProxy.Verify(
            x => x.SendCoreAsync("alarm", It.Is<object[]>(args => args[0] == alarm), default),
            Times.Once
        );
    }

    [Fact]
    [Parity]
    public async Task BroadcastUrgentAlarmAsync_ShouldSendToAlarmSubscribers()
    {
        // Arrange
        var urgentAlarm = new NotificationBase
        {
            Title = "Urgent Alarm",
            Message = "Urgent alarm message",
            Level = 2,
        };

        // Act
        await _service.BroadcastUrgentAlarmAsync(urgentAlarm);

        // Assert
        _mockAlarmClients.Verify(x => x.Group("alarm-subscribers"), Times.Once);
        _mockAlarmGroupProxy.Verify(
            x =>
                x.SendCoreAsync(
                    "urgent_alarm",
                    It.Is<object[]>(args => args[0] == urgentAlarm),
                    default
                ),
            Times.Once
        );
    }

    [Fact]
    [Parity]
    public async Task BroadcastClearAlarmAsync_ShouldSendToAlarmSubscribers()
    {
        // Arrange
        var clearAlarm = new NotificationBase
        {
            Clear = true,
            Title = "Alarm Cleared",
            Message = "Alarm has been cleared",
            Group = "default",
        };

        // Act
        await _service.BroadcastClearAlarmAsync(clearAlarm);

        // Assert
        _mockAlarmClients.Verify(x => x.Group("alarm-subscribers"), Times.Once);
        _mockAlarmGroupProxy.Verify(
            x =>
                x.SendCoreAsync(
                    "clear_alarm",
                    It.Is<object[]>(args => args[0] == clearAlarm),
                    default
                ),
            Times.Once
        );
    }

    [Fact]
    [Parity]
    public async Task BroadcastAnnouncementAsync_ShouldSendToAlarmSubscribers()
    {
        // Arrange
        var announcement = new NotificationBase
        {
            Title = "Announcement",
            Message = "Test announcement",
            IsAnnouncement = true,
        };

        // Act
        await _service.BroadcastAnnouncementAsync(announcement);

        // Assert
        _mockAlarmClients.Verify(x => x.Group("alarm-subscribers"), Times.Once);
        _mockAlarmGroupProxy.Verify(
            x =>
                x.SendCoreAsync(
                    "announcement",
                    It.Is<object[]>(args => args[0] == announcement),
                    default
                ),
            Times.Once
        );
    }

    [Fact]
    [Parity]
    public async Task BroadcastRetroUpdateAsync_ShouldSendToSpecificClient()
    {
        // Arrange
        var connectionId = "test-connection-id";
        var retroData = new { entries = new[] { new { id = "test" } } };
        var mockClientProxy = new Mock<ISingleClientProxy>();
        _mockDataClients.Setup(x => x.Client(connectionId)).Returns(mockClientProxy.Object);

        // Act
        await _service.BroadcastRetroUpdateAsync(connectionId, retroData);

        // Assert
        _mockDataClients.Verify(x => x.Client(connectionId), Times.Once);
        mockClientProxy.Verify(
            x =>
                x.SendCoreAsync(
                    "retroUpdate",
                    It.Is<object[]>(args => args[0] == retroData),
                    default
                ),
            Times.Once
        );
    }

    [Fact]
    [Parity]
    public async Task BroadcastPasswordResetRequestAsync_ShouldSendToAdminGroup()
    {
        // Act
        await _service.BroadcastPasswordResetRequestAsync();

        // Assert
        _mockDataClients.Verify(x => x.Group("admin"), Times.Once);
        _mockDataGroupProxy.Verify(
            x =>
                x.SendCoreAsync(
                    "passwordResetRequested",
                    It.Is<object[]>(args => args.Length == 0),
                    default
                ),
            Times.Once
        );
    }

    [Fact]
    [Parity]
    public async Task BroadcastStorageCreateAsync_WithException_ShouldLogError()
    {
        // Arrange
        _mockDataGroupProxy
            .Setup(x => x.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), default))
            .ThrowsAsync(new Exception("Test exception"));

        var collectionName = "treatments";
        var data = new { test = "data" };

        // Act
        await _service.BroadcastStorageCreateAsync(collectionName, data);

        // Assert
        _mockLogger.Verify(
            x =>
                x.Log(
                    LogLevel.Error,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>(
                        (v, t) => v.ToString()!.Contains("Error broadcasting storage create event")
                    ),
                    It.IsAny<Exception?>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()
                ),
            Times.Once
        );
    }
}
