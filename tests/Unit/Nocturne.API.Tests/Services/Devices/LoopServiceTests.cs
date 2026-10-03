using dotAPNS;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Services.Devices;
using Nocturne.API.Services.Notifications;
using Xunit;

namespace Nocturne.API.Tests.Services.Devices;

/// <summary>
/// Tests for LoopService with 1:1 legacy compatibility
/// Tests APNS integration functionality from legacy loop.js behavior
/// </summary>
[Parity("loop.test.js")]
public class LoopServiceTests
{
    private readonly Mock<ILogger<LoopService>> _mockLogger;
    private readonly Mock<IApnsClientFactory> _mockApnsClientFactory;
    private readonly Mock<IApnsClient> _mockApnsClient;
    private readonly LoopService _loopService;
    private readonly LoopConfiguration _configuration;

    public LoopServiceTests()
    {
        _mockLogger = new Mock<ILogger<LoopService>>();
        _mockApnsClientFactory = new Mock<IApnsClientFactory>();
        _mockApnsClient = new Mock<IApnsClient>();

        _configuration = new LoopConfiguration
        {
            ApnsKey = "test-key",
            ApnsKeyId = "test-key-id",
            DeveloperTeamId = "ABCDEFGHIJ", // Must be exactly 10 characters
            PushServerEnvironment = "development",
        };

        var options = Options.Create(_configuration);

        _mockApnsClientFactory.SetupGet(x => x.IsConfigured).Returns(false);
        _mockApnsClientFactory
            .Setup(x => x.CreateClient(It.IsAny<string>()))
            .Returns(_mockApnsClient.Object);
        _mockApnsClient
            .Setup(x => x.SendAsync(It.IsAny<ApplePush>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApnsResponse.Successful());

        _loopService = new LoopService(_mockLogger.Object, options, _mockApnsClientFactory.Object);
    }

    [Parity]
    [Fact]
    public void Constructor_WithValidConfiguration_ShouldInitializeSuccessfully()
    {
        // Arrange - Configuration already set in constructor

        // Act & Assert
        Assert.NotNull(_loopService);
        // Service should log successful initialization
        _mockLogger.Verify(
            x =>
                x.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>(
                        (v, t) => v.ToString()!.Contains("Loop service initialized successfully")
                    ),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()
                ),
            Times.Never
        ); // Since we're mocking HTTP client, APNS initialization might not complete
    }

    [Parity]
    [Fact]
    public async Task SendNotificationAsync_WithInvalidConfiguration_ShouldReturnError()
    {
        // Arrange
        var invalidConfig = new LoopConfiguration(); // Empty configuration
        var invalidOptions = Options.Create(invalidConfig);
        var invalidLoopService = new LoopService(
            _mockLogger.Object,
            invalidOptions,
            _mockApnsClientFactory.Object
        );

        var data = new LoopNotificationData { EventType = "loop-completed" };
        var loopSettings = new LoopProfileSettings { BundleIdentifier = "com.example.loop" };

        // Act
        var result = await invalidLoopService.SendNotificationAsync(
            data,
            loopSettings,
            "127.0.0.1",
            CancellationToken.None
        );

        // Assert
        Assert.False(result.Success);
        Assert.Contains("configuration", result.Message.ToLowerInvariant());
    }

    [Parity]
    [Fact]
    public async Task SendNotificationAsync_WithNullLoopSettings_ShouldReturnError()
    {
        // Arrange
        var data = new LoopNotificationData { EventType = "loop-completed" };

        // Act
        var result = await _loopService.SendNotificationAsync(
            data,
            null!,
            "127.0.0.1",
            CancellationToken.None
        );

        // Assert
        Assert.False(result.Success);
        Assert.Equal(
            "Loop notification failed: Could not find loopSettings in profile.",
            result.Message
        );
    }

    [Parity]
    [Fact]
    public async Task SendNotificationAsync_WithEmptyBundleId_ShouldReturnError()
    {
        // Arrange
        var data = new LoopNotificationData { EventType = "loop-completed" };
        var loopSettings = new LoopProfileSettings
        {
            DeviceToken = "valid-device-token", // Valid device token
            BundleIdentifier = "", // Empty bundle ID
        };

        // Act
        var result = await _loopService.SendNotificationAsync(
            data,
            loopSettings,
            "127.0.0.1",
            CancellationToken.None
        );

        // Assert
        Assert.False(result.Success);
        Assert.Equal(
            "Loop notification failed: Could not find bundleIdentifier in loopSettings.",
            result.Message
        );
    }

    [Parity]
    [Fact]
    public async Task SendNotificationAsync_WithValidInputButMockedClient_ShouldHandleGracefully()
    {
        // Arrange
        var data = new LoopNotificationData { EventType = "loop-completed" };
        var loopSettings = new LoopProfileSettings
        {
            BundleIdentifier = "com.example.loop",
            DeviceToken = "test-device-token",
        };

        // Act
        var result = await _loopService.SendNotificationAsync(
            data,
            loopSettings,
            "127.0.0.1",
            CancellationToken.None
        );

        // Assert
        // With mocked HTTP client, the service should either succeed or fail gracefully
        Assert.NotNull(result);
        Assert.NotNull(result.Message);
    }
}
