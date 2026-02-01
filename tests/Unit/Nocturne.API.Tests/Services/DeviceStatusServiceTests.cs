using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Services;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Cache.Abstractions;
using Nocturne.Infrastructure.Data.Abstractions;
using Xunit;

namespace Nocturne.API.Tests.Services;

/// <summary>
/// Unit tests for DeviceStatusService domain service with WebSocket broadcasting
/// </summary>
[Parity("api.devicestatus.test.js")]
public class DeviceStatusServiceTests
{
    private readonly Mock<IPostgreSqlService> _mockPostgreSqlService;
    private readonly Mock<ISignalRBroadcastService> _mockSignalRBroadcastService;
    private readonly Mock<ICacheService> _mockCacheService;
    private readonly Mock<ILogger<DeviceStatusService>> _mockLogger;
    private readonly DeviceStatusService _deviceStatusService;

    public DeviceStatusServiceTests()
    {
        _mockPostgreSqlService = new Mock<IPostgreSqlService>();
        _mockSignalRBroadcastService = new Mock<ISignalRBroadcastService>();
        _mockCacheService = new Mock<ICacheService>();
        _mockLogger = new Mock<ILogger<DeviceStatusService>>();

        _deviceStatusService = new DeviceStatusService(
            _mockPostgreSqlService.Object,
            _mockSignalRBroadcastService.Object,
            _mockCacheService.Object,
            _mockLogger.Object
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public async Task GetDeviceStatusAsync_WithoutParameters_ReturnsAllDeviceStatus()
    {
        // Arrange
        var expectedDeviceStatus = new List<DeviceStatus>
        {
            new DeviceStatus
            {
                Id = "1",
                Device = "dexcom",
                Mills = 1234567890,
            },
            new DeviceStatus
            {
                Id = "2",
                Device = "loop",
                Mills = 1234567880,
            },
        };

        _mockCacheService
            .Setup(x =>
                x.GetAsync<IEnumerable<DeviceStatus>>(
                    "devicestatus:current",
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync((IEnumerable<DeviceStatus>?)null);

        _mockPostgreSqlService
            .Setup(x => x.GetDeviceStatusAsync(10, 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedDeviceStatus);

        // Act
        var result = await _deviceStatusService.GetDeviceStatusAsync(
            cancellationToken: CancellationToken.None
        );

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Count());
        Assert.Equal(expectedDeviceStatus, result);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public async Task GetDeviceStatusAsync_WithParameters_ReturnsFilteredDeviceStatus()
    {
        // Arrange
        var find = "{\"device\":\"dexcom\"}";
        var count = 10;
        var skip = 0;
        var expectedDeviceStatus = new List<DeviceStatus>
        {
            new DeviceStatus
            {
                Id = "1",
                Device = "dexcom",
                Mills = 1234567890,
            },
        };

        // When find is provided, GetDeviceStatusWithAdvancedFilterAsync is called
        _mockPostgreSqlService
            .Setup(x => x.GetDeviceStatusWithAdvancedFilterAsync(
                count, skip, find, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedDeviceStatus);

        // Act
        var result = await _deviceStatusService.GetDeviceStatusAsync(
            find,
            count,
            skip,
            CancellationToken.None
        );

        // Assert
        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal(expectedDeviceStatus.First().Id, result.First().Id);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public async Task GetDeviceStatusByIdAsync_WithValidId_ReturnsDeviceStatus()
    {
        // Arrange
        var deviceStatusId = "60a1b2c3d4e5f6789012345";
        var expectedDeviceStatus = new DeviceStatus
        {
            Id = deviceStatusId,
            Device = "dexcom",
            Mills = 1234567890,
        };

        _mockPostgreSqlService
            .Setup(x => x.GetDeviceStatusByIdAsync(deviceStatusId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedDeviceStatus);

        // Act
        var result = await _deviceStatusService.GetDeviceStatusByIdAsync(
            deviceStatusId,
            CancellationToken.None
        );

        // Assert
        Assert.NotNull(result);
        Assert.Equal(deviceStatusId, result.Id);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public async Task GetDeviceStatusByIdAsync_WithInvalidId_ReturnsNull()
    {
        // Arrange
        var deviceStatusId = "invalidid";

        _mockPostgreSqlService
            .Setup(x => x.GetDeviceStatusByIdAsync(deviceStatusId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((DeviceStatus?)null);

        // Act
        var result = await _deviceStatusService.GetDeviceStatusByIdAsync(
            deviceStatusId,
            CancellationToken.None
        );

        // Assert
        Assert.Null(result);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public async Task CreateDeviceStatusAsync_WithValidDeviceStatus_ReturnsCreatedDeviceStatusAndBroadcasts()
    {
        // Arrange
        var deviceStatusEntries = new List<DeviceStatus>
        {
            new DeviceStatus { Device = "dexcom", Mills = 1234567890 },
            new DeviceStatus { Device = "loop", Mills = 1234567880 },
        };

        var createdDeviceStatus = deviceStatusEntries
            .Select(d => new DeviceStatus
            {
                Id = Guid.NewGuid().ToString(),
                Device = d.Device,
                Mills = d.Mills,
            })
            .ToList();

        _mockPostgreSqlService
            .Setup(x =>
                x.CreateDeviceStatusAsync(deviceStatusEntries, It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(createdDeviceStatus);

        // Act
        var result = await _deviceStatusService.CreateDeviceStatusAsync(
            deviceStatusEntries,
            CancellationToken.None
        );

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Count());
        _mockPostgreSqlService.Verify(
            x => x.CreateDeviceStatusAsync(deviceStatusEntries, It.IsAny<CancellationToken>()),
            Times.Once
        );
        _mockSignalRBroadcastService.Verify(
            x => x.BroadcastStorageCreateAsync("devicestatus", It.IsAny<object>()),
            Times.Exactly(2)
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public async Task UpdateDeviceStatusAsync_WithValidDeviceStatus_ReturnsUpdatedDeviceStatusAndBroadcasts()
    {
        // Arrange
        var deviceStatusId = "60a1b2c3d4e5f6789012345";
        var deviceStatus = new DeviceStatus
        {
            Id = deviceStatusId,
            Device = "dexcom",
            Mills = 1234567890,
        };
        var updatedDeviceStatus = new DeviceStatus
        {
            Id = deviceStatusId,
            Device = "dexcom",
            Mills = 1234567900,
        };

        _mockPostgreSqlService
            .Setup(x =>
                x.UpdateDeviceStatusAsync(
                    deviceStatusId,
                    deviceStatus,
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(updatedDeviceStatus);

        // Act
        var result = await _deviceStatusService.UpdateDeviceStatusAsync(
            deviceStatusId,
            deviceStatus,
            CancellationToken.None
        );

        // Assert
        Assert.NotNull(result);
        Assert.Equal(deviceStatusId, result.Id);
        Assert.Equal(1234567900, result.Mills);
        _mockPostgreSqlService.Verify(
            x =>
                x.UpdateDeviceStatusAsync(
                    deviceStatusId,
                    deviceStatus,
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
        _mockSignalRBroadcastService.Verify(
            x => x.BroadcastStorageUpdateAsync("devicestatus", It.IsAny<object>()),
            Times.Once
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public async Task UpdateDeviceStatusAsync_WithInvalidId_ReturnsNullAndDoesNotBroadcast()
    {
        // Arrange
        var deviceStatusId = "invalidid";
        var deviceStatus = new DeviceStatus { Device = "dexcom", Mills = 1234567890 };

        _mockPostgreSqlService
            .Setup(x =>
                x.UpdateDeviceStatusAsync(
                    deviceStatusId,
                    deviceStatus,
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync((DeviceStatus?)null);

        // Act
        var result = await _deviceStatusService.UpdateDeviceStatusAsync(
            deviceStatusId,
            deviceStatus,
            CancellationToken.None
        );

        // Assert
        Assert.Null(result);
        _mockSignalRBroadcastService.Verify(
            x => x.BroadcastStorageUpdateAsync(It.IsAny<string>(), It.IsAny<object>()),
            Times.Never
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public async Task DeleteDeviceStatusAsync_WithValidId_ReturnsTrueAndBroadcasts()
    {
        // Arrange
        var deviceStatusId = "60a1b2c3d4e5f6789012345";
        var deviceStatusToDelete = new DeviceStatus
        {
            Id = deviceStatusId,
            Device = "dexcom",
            Mills = 1234567890,
        };

        _mockPostgreSqlService
            .Setup(x => x.GetDeviceStatusByIdAsync(deviceStatusId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(deviceStatusToDelete);

        _mockPostgreSqlService
            .Setup(x => x.DeleteDeviceStatusAsync(deviceStatusId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        var result = await _deviceStatusService.DeleteDeviceStatusAsync(
            deviceStatusId,
            CancellationToken.None
        );

        // Assert
        Assert.True(result);
        _mockPostgreSqlService.Verify(
            x => x.DeleteDeviceStatusAsync(deviceStatusId, It.IsAny<CancellationToken>()),
            Times.Once
        );
        _mockSignalRBroadcastService.Verify(
            x => x.BroadcastStorageDeleteAsync("devicestatus", It.IsAny<object>()),
            Times.Once
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public async Task DeleteDeviceStatusAsync_WithInvalidId_ReturnsFalseAndDoesNotBroadcast()
    {
        // Arrange
        var deviceStatusId = "invalidid";

        _mockPostgreSqlService
            .Setup(x => x.DeleteDeviceStatusAsync(deviceStatusId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Act
        var result = await _deviceStatusService.DeleteDeviceStatusAsync(
            deviceStatusId,
            CancellationToken.None
        );

        // Assert
        Assert.False(result);
        _mockSignalRBroadcastService.Verify(
            x => x.BroadcastStorageDeleteAsync(It.IsAny<string>(), It.IsAny<object>()),
            Times.Never
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public async Task DeleteDeviceStatusAsync_WithValidFilter_ReturnsDeletedCountAndBroadcasts()
    {
        // Arrange
        var find = "{\"device\":\"dexcom\"}";
        var deletedCount = 3L;

        _mockPostgreSqlService
            .Setup(x => x.BulkDeleteDeviceStatusAsync(find, It.IsAny<CancellationToken>()))
            .ReturnsAsync(deletedCount);

        // Act
        var result = await _deviceStatusService.DeleteDeviceStatusEntriesAsync(
            find,
            CancellationToken.None
        );

        // Assert
        Assert.Equal(deletedCount, result);
        _mockPostgreSqlService.Verify(
            x => x.BulkDeleteDeviceStatusAsync(find, It.IsAny<CancellationToken>()),
            Times.Once
        );
        _mockSignalRBroadcastService.Verify(
            x => x.BroadcastStorageDeleteAsync("devicestatus", It.IsAny<object>()),
            Times.Once
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public async Task DeleteDeviceStatusAsync_WithNoMatches_ReturnsZeroAndDoesNotBroadcast()
    {
        // Arrange
        var find = "{\"device\":\"nonexistent\"}";
        var deletedCount = 0L;

        _mockPostgreSqlService
            .Setup(x => x.BulkDeleteDeviceStatusAsync(find, It.IsAny<CancellationToken>()))
            .ReturnsAsync(deletedCount);

        // Act
        var result = await _deviceStatusService.DeleteDeviceStatusEntriesAsync(
            find,
            CancellationToken.None
        );

        // Assert
        Assert.Equal(0, result);
        _mockSignalRBroadcastService.Verify(
            x => x.BroadcastStorageDeleteAsync(It.IsAny<string>(), It.IsAny<object>()),
            Times.Never
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public async Task CreateDeviceStatusAsync_WithException_ThrowsException()
    {
        // Arrange
        var deviceStatusEntries = new List<DeviceStatus>
        {
            new DeviceStatus { Device = "dexcom", Mills = 1234567890 },
        };

        _mockPostgreSqlService
            .Setup(x =>
                x.CreateDeviceStatusAsync(deviceStatusEntries, It.IsAny<CancellationToken>())
            )
            .ThrowsAsync(new InvalidOperationException("Processing failed"));

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _deviceStatusService.CreateDeviceStatusAsync(
                deviceStatusEntries,
                CancellationToken.None
            )
        );
        _mockSignalRBroadcastService.Verify(
            x => x.BroadcastStorageCreateAsync(It.IsAny<string>(), It.IsAny<object>()),
            Times.Never
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public async Task GetRecentDeviceStatusAsync_WithDeviceStatus_ReturnsLatestDeviceStatus()
    {
        // Arrange
        var expectedDeviceStatus = new List<DeviceStatus>
        {
            new DeviceStatus
            {
                Id = "1",
                Device = "dexcom",
                Mills = 1234567890,
            },
        };

        _mockPostgreSqlService
            .Setup(x =>
                x.GetDeviceStatusAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(expectedDeviceStatus);

        // Act
        var result = await _deviceStatusService.GetRecentDeviceStatusAsync(
            cancellationToken: CancellationToken.None
        );

        // Assert
        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal(expectedDeviceStatus.First().Id, result.First().Id);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public async Task GetRecentDeviceStatusAsync_WithNoDeviceStatus_ReturnsEmpty()
    {
        // Arrange
        var emptyDeviceStatus = new List<DeviceStatus>();

        _mockPostgreSqlService
            .Setup(x =>
                x.GetDeviceStatusAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(emptyDeviceStatus);

        // Act
        var result = await _deviceStatusService.GetRecentDeviceStatusAsync(
            cancellationToken: CancellationToken.None
        );

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
    }
}
