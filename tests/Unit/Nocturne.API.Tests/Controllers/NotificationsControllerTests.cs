using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V1;
using Nocturne.API.Services.Alerts;
using Nocturne.Core.Contracts.Notifications;

namespace Nocturne.API.Tests.Controllers;

/// <summary>
/// Unit tests for NotificationsController V1 endpoints
/// Tests the Phase 8.1 notification endpoints with 1:1 legacy compatibility
/// </summary>
public class NotificationsControllerTests
{
    private readonly Mock<INotificationV1Service> _mockNotificationService;
    private readonly Mock<IAlarmAckHandler> _mockAckHandler = new();
    private readonly Mock<ILogger<NotificationsController>> _mockLogger;
    private readonly NotificationsController _controller;

    public NotificationsControllerTests()
    {
        _mockNotificationService = new Mock<INotificationV1Service>();
        _mockLogger = new Mock<ILogger<NotificationsController>>();
        _controller = new NotificationsController(
            _mockNotificationService.Object,
            _mockAckHandler.Object,
            _mockLogger.Object
        );

        // Setup HttpContext for remote IP address
        var httpContext = new DefaultHttpContext();
        httpContext.Connection.RemoteIpAddress = IPAddress.Parse("127.0.0.1");
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
    }

    private static void AssertPlainText(IActionResult result, int status, string body)
    {
        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal(status, content.StatusCode);
        Assert.Equal(body, content.Content);
        Assert.StartsWith("text/plain", content.ContentType);
    }

    private void SetupAck(bool success = true) =>
        _mockAckHandler
            .Setup(h => h.AckAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationAckResponse { Success = success, Message = "ack result" });

    [Fact]
    public async Task AckNotification_WithQueryParameters_AcksThroughTheAlarmAckHandler()
    {
        SetupAck();

        var result = await _controller.AckNotification(2, "alarms", "1800000", CancellationToken.None);

        AssertPlainText(result, 200, "OK");
        _mockAckHandler.Verify(h => h.AckAsync(2, "alarms", 1800000, It.IsAny<CancellationToken>()), Times.Once);
        _mockNotificationService.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task AckNotification_WithoutGroup_AcksTheDefaultGroup(string? group)
    {
        SetupAck();

        await _controller.AckNotification(1, group, null, CancellationToken.None);

        _mockAckHandler.Verify(
            h => h.AckAsync(1, "default", It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(null, NotificationAckRequest.DefaultSilenceTimeMs)]
    [InlineData("", NotificationAckRequest.DefaultSilenceTimeMs)]
    [InlineData("0", NotificationAckRequest.DefaultSilenceTimeMs)]
    [InlineData("abc", NotificationAckRequest.DefaultSilenceTimeMs)]
    [InlineData("NaN", NotificationAckRequest.DefaultSilenceTimeMs)]
    [InlineData("1800000", 1800000)]
    [InlineData("1800000.9", 1800000)]
    [InlineData("-5000", -5000)]
    [InlineData("99999999999", int.MaxValue)]
    [InlineData("Infinity", int.MaxValue)]
    [InlineData("-99999999999", int.MinValue)]
    public async Task AckNotification_ReadsTimeAsNightscoutDoes(string? time, int expected)
    {
        SetupAck();

        var result = await _controller.AckNotification(1, "default", time, CancellationToken.None);

        AssertPlainText(result, 200, "OK");
        _mockAckHandler.Verify(
            h => h.AckAsync(1, "default", expected, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(-1)]
    public async Task AckNotification_WithUnknownLevel_AnswersBadRequestWithoutAcking(int level)
    {
        var result = await _controller.AckNotification(level, "default", null, CancellationToken.None);

        AssertPlainText(result, 400, "Level must be 1 (WARN) or 2 (URGENT)");
        _mockAckHandler.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AckNotification_WhenAlreadySnoozed_StillAnswersOk()
    {
        SetupAck(success: false);

        var result = await _controller.AckNotification(1, "default", null, CancellationToken.None);

        AssertPlainText(result, 200, "OK");
    }

    [Fact]
    public async Task GetAdminNotifies_ReturnsOkWithNotifications()
    {
        // Arrange
        var expectedResponse = new AdminNotifiesResponse
        {
            Status = 200,
            Message = new AdminNotifiesMessage
            {
                Notifies = new List<AdminNotification>
                {
                    new AdminNotification
                    {
                        Title = "Test Notification",
                        Message = "Test message",
                        Count = 1,
                        LastRecorded = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        Persistent = false,
                    },
                },
                NotifyCount = 1,
            },
        };
        _mockNotificationService
            .Setup(s => s.GetAdminNotifiesAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResponse);

        // Act
        var result = await _controller.GetAdminNotifies(CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<AdminNotifiesResponse>(okResult.Value);
        Assert.Equal(200, response.Status);
        Assert.Equal(1, response.Message.NotifyCount);
        Assert.Single(response.Message.Notifies);
        _mockNotificationService.Verify(
            s => s.GetAdminNotifiesAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    public async Task AddAdminNotification_WithValidNotification_ReturnsOkResult()
    {
        // Arrange
        var notification = new AdminNotification
        {
            Title = "Test Title",
            Message = "Test message",
            Persistent = false,
        };

        var expectedResponse = new NotificationAckResponse
        {
            Success = true,
            Message = "Admin notification added successfully",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        _mockNotificationService
            .Setup(s =>
                s.AddAdminNotificationAsync(
                    It.IsAny<AdminNotification>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(expectedResponse);

        // Act
        var result = await _controller.AddAdminNotification(notification, CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<NotificationAckResponse>(okResult.Value);
        Assert.True(response.Success);
        Assert.Equal("Admin notification added successfully", response.Message);
        _mockNotificationService.Verify(
            s =>
                s.AddAdminNotificationAsync(
                    It.IsAny<AdminNotification>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task AddAdminNotification_WithNullNotification_ReturnsBadRequest()
    {
        // Arrange
        AdminNotification? notification = null;

        // Act
        var result = await _controller.AddAdminNotification(notification!, CancellationToken.None);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result.Result);
        var response = Assert.IsType<NotificationAckResponse>(badRequestResult.Value);
        Assert.False(response.Success);
        Assert.Equal("Request body is required", response.Message);
    }

    [Fact]
    public async Task ClearAllAdminNotifications_ReturnsOkResult()
    {
        // Arrange
        _mockNotificationService
            .Setup(s => s.ClearAllAdminNotificationsAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _controller.ClearAllAdminNotifications(CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<NotificationAckResponse>(okResult.Value);
        Assert.True(response.Success);
        Assert.Equal("All admin notifications cleared successfully", response.Message);
        _mockNotificationService.Verify(
            s => s.ClearAllAdminNotificationsAsync(It.IsAny<CancellationToken>()),
            Times.Once
        );
    }
}
