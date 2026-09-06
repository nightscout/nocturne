using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Notifications;
using Nocturne.Core.Contracts.Notifications;
using Xunit;

namespace Nocturne.API.Tests.Services.Notifications;

/// <summary>
/// Unit tests for PushoverService
/// Tests the Pushover notification sending functionality with 1:1 legacy compatibility
/// </summary>
[Parity("pushover.test.js")]
public class PushoverServiceTests
{
    private readonly Mock<HttpClient> _mockHttpClient;
    private readonly Mock<ILogger<PushoverService>> _mockLogger;
    private readonly Mock<INotificationV1Service> _mockNotificationService;
    private readonly Mock<IConfiguration> _mockConfiguration;
    private readonly PushoverService _service;

    public PushoverServiceTests()
    {
        _mockHttpClient = new Mock<HttpClient>();
        _mockLogger = new Mock<ILogger<PushoverService>>();
        _mockNotificationService = new Mock<INotificationV1Service>();
        _mockConfiguration = new Mock<IConfiguration>();

        // Setup configuration
        _mockConfiguration.Setup(c => c["Pushover:ApiToken"]).Returns("test-api-token");
        _mockConfiguration.Setup(c => c["Pushover:UserKey"]).Returns("test-user-key");

        _service = new PushoverService(
            _mockHttpClient.Object,
            _mockLogger.Object,
            _mockNotificationService.Object,
            _mockConfiguration.Object,
            Options.Create(new BaseDomainOptions { BaseDomain = "nocturne.example.com" })
        );
    }

    [Fact]
    public void CreateAlarmNotification_WithWarnLevel_SetsCorrectPriority()
    {
        // Arrange
        var level = 1; // WARN
        var group = "test-group";
        var title = "Test Warning";
        var message = "This is a test warning";

        // Act
        var result = _service.CreateAlarmNotification(level, group, title, message);

        // Assert
        Assert.Equal(level, result.Level);
        Assert.Equal(group, result.Group);
        Assert.Equal(title, result.Title);
        Assert.Equal(message, result.Message);
        Assert.Equal(0, result.Priority); // Normal priority for WARN
        Assert.Equal("default", result.Sound);
    }

    [Fact]
    public void CreateAlarmNotification_WithUrgentLevel_SetsEmergencyPriority()
    {
        // Arrange
        var level = 2; // URGENT
        var group = "test-group";
        var title = "Test Urgent";
        var message = "This is a test urgent alarm";

        // Act
        var result = _service.CreateAlarmNotification(level, group, title, message);

        // Assert
        Assert.Equal(level, result.Level);
        Assert.Equal(group, result.Group);
        Assert.Equal(title, result.Title);
        Assert.Equal(message, result.Message);
        Assert.Equal(2, result.Priority); // Emergency priority for URGENT
        Assert.Equal("persistent", result.Sound);
        Assert.Equal(60, result.Retry);
        Assert.Equal(3600, result.Expire);
    }

    [Fact]
    public void CreateAlarmNotification_WithCustomSound_UsesCustomSound()
    {
        // Arrange
        var level = 1;
        var group = "test-group";
        var title = "Test";
        var message = "Test message";
        var customSound = "siren";

        // Act
        var result = _service.CreateAlarmNotification(level, group, title, message, customSound);

        // Assert
        Assert.Equal(customSound, result.Sound);
    }

    [Fact]
    public async Task SendNotificationAsync_WithMissingApiToken_ReturnsFailure()
    {
        // Arrange
        _mockConfiguration.Setup(c => c["Pushover:ApiToken"]).Returns((string?)null);
        _mockConfiguration.Setup(c => c["PUSHOVER_API_TOKEN"]).Returns((string?)null);

        var request = new PushoverNotificationRequest { Title = "Test", Message = "Test message" };

        // Act
        var result = await _service.SendNotificationAsync(request, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Equal("Pushover API token not configured", result.Error);
    }

    [Fact]
    public async Task SendNotificationAsync_WithMissingUserKey_ReturnsFailure()
    {
        // Arrange
        _mockConfiguration.Setup(c => c["Pushover:UserKey"]).Returns((string?)null);
        _mockConfiguration.Setup(c => c["PUSHOVER_USER_KEY"]).Returns((string?)null);

        var request = new PushoverNotificationRequest { Title = "Test", Message = "Test message" };

        // Act
        var result = await _service.SendNotificationAsync(request, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Equal("Pushover user key not configured", result.Error);
    }
}
