using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V2;
using Nocturne.Core.Contracts.Notifications;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V2;

public class NotificationsControllerTests
{
    private readonly Mock<INotificationV2Service> _mockNotificationService;
    private readonly Mock<ILogger<NotificationsController>> _mockLogger;
    private readonly NotificationsController _controller;

    public NotificationsControllerTests()
    {
        _mockNotificationService = new Mock<INotificationV2Service>();
        _mockLogger = new Mock<ILogger<NotificationsController>>();
        _controller = new NotificationsController(
            _mockNotificationService.Object,
            _mockLogger.Object
        );
    }

    private LoopNotificationData? _sent;

    private void GivenRequestBody(string contentType, string body)
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = contentType;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Request.ContentLength = context.Request.Body.Length;
        _controller.ControllerContext = new ControllerContext { HttpContext = context };
    }

    private void GivenSendResult(bool success, string message) =>
        _mockNotificationService
            .Setup(s =>
                s.SendLoopNotificationAsync(
                    It.IsAny<LoopNotificationData>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback<LoopNotificationData, string, CancellationToken>((data, _, _) => _sent = data)
            .ReturnsAsync(new LoopNotificationResponse { Success = success, Message = message });

    [Fact]
    public async Task SendLoopNotification_NightscoutJsonBody_IsSentAndAnswersOk()
    {
        GivenSendResult(true, "Loop notification sent successfully");
        GivenRequestBody(
            "application/json",
            """{"eventType":"Remote Carbs Entry","remoteCarbs":"20","remoteAbsorption":3,"otp":"123456","enteredBy":"caregiver","created_at":"2026-09-30T01:02:03Z"}"""
        );

        var result = await _controller.SendLoopNotification(CancellationToken.None);

        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal(StatusCodes.Status200OK, content.StatusCode);
        Assert.Equal("OK", content.Content);
        Assert.NotNull(_sent);
        Assert.Equal("Remote Carbs Entry", _sent.EventType);
        Assert.Equal("20", _sent.RemoteCarbs);
        Assert.Equal("3", _sent.RemoteAbsorption);
        Assert.Equal("123456", _sent.Otp);
        Assert.Equal("caregiver", _sent.EnteredBy);
        Assert.Equal("2026-09-30T01:02:03Z", _sent.CreatedAt);
    }

    [Fact]
    public async Task SendLoopNotification_CareportalFormBody_IsSent()
    {
        GivenSendResult(true, "Loop notification sent successfully");
        GivenRequestBody(
            "application/x-www-form-urlencoded",
            "eventType=Temporary+Override&reason=exercise&reasonDisplay=Pre-run+exercise&duration=60&notes=&enteredBy=parent&created_at=2026-09-30T01%3A02%3A03Z"
        );

        var result = await _controller.SendLoopNotification(CancellationToken.None);

        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal(StatusCodes.Status200OK, content.StatusCode);
        Assert.NotNull(_sent);
        Assert.Equal("Temporary Override", _sent.EventType);
        Assert.Equal("exercise", _sent.Reason);
        Assert.Equal("Pre-run exercise", _sent.ReasonDisplay);
        Assert.Equal("60", _sent.Duration);
        Assert.Equal("parent", _sent.EnteredBy);
        Assert.Equal("2026-09-30T01:02:03Z", _sent.CreatedAt);
    }

    [Fact]
    public async Task SendLoopNotification_NothingSent_AnswersServerErrorWithTheReason()
    {
        GivenSendResult(
            false,
            "Loop notification failed: Could not find deviceToken in loopSettings."
        );
        GivenRequestBody("application/json", """{"eventType":"Temporary Override Cancel"}""");

        var result = await _controller.SendLoopNotification(CancellationToken.None);

        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal(StatusCodes.Status500InternalServerError, content.StatusCode);
        Assert.Equal(
            "Loop notification failed: Could not find deviceToken in loopSettings.",
            content.Content
        );
    }

    [Fact]
    public async Task SendLoopNotification_EmptyBody_ReachesTheServiceWithNoEventType()
    {
        GivenSendResult(false, "Loop notification failed: Unhandled event type: ");
        GivenRequestBody("application/json", "");

        var result = await _controller.SendLoopNotification(CancellationToken.None);

        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal(StatusCodes.Status500InternalServerError, content.StatusCode);
        Assert.NotNull(_sent);
        Assert.Null(_sent.EventType);
    }

    [Fact]
    public async Task SendLoopNotification_FormOverTheKeyLengthLimit_AnswersBadRequest()
    {
        GivenRequestBody("application/x-www-form-urlencoded", new string('k', 5000) + "=1");

        var result = await _controller.SendLoopNotification(CancellationToken.None);

        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, content.StatusCode);
        Assert.Equal("Malformed request body", content.Content);
        _mockNotificationService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SendLoopNotification_MalformedJson_AnswersBadRequest()
    {
        GivenRequestBody("application/json", "{\"eventType\":");

        var result = await _controller.SendLoopNotification(CancellationToken.None);

        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, content.StatusCode);
        _mockNotificationService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ProcessNotification_WithValidRequest_ReturnsOkResult()
    {
        // Arrange
        var request = new NotificationBase
        {
            Title = "Test Notification",
            Message = "This is a test notification",
        };

        var expectedResponse = new NotificationV2Response
        {
            Success = true,
            Message = "Notification processed successfully",
        };

        _mockNotificationService
            .Setup(s =>
                s.ProcessNotificationAsync(
                    It.IsAny<NotificationBase>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(expectedResponse);

        // Act
        var result = await _controller.ProcessNotification(request, CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<NotificationV2Response>(okResult.Value);
        Assert.True(response.Success);
        Assert.Equal("Notification processed successfully", response.Message);
    }

    [Fact]
    public async Task GetNotificationStatus_ReturnsOkResult()
    {
        // Arrange
        var expectedResponse = new
        {
            status = "active",
            version = "v2",
            supported_types = new[] { "loop", "announcement", "alarm", "info" },
        };

        _mockNotificationService
            .Setup(s => s.GetNotificationStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResponse);

        // Act
        var result = await _controller.GetNotificationStatus(CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(okResult.Value);
    }

    [Fact]
    public async Task ProcessNotification_WithNullRequest_ReturnsBadRequest()
    {
        // Arrange
        NotificationBase? request = null;

        // Act
        var result = await _controller.ProcessNotification(request!, CancellationToken.None);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result.Result);
        var response = Assert.IsType<NotificationV2Response>(badRequestResult.Value);
        Assert.False(response.Success);
        Assert.Equal("Request body is required", response.Message);
    }
}
