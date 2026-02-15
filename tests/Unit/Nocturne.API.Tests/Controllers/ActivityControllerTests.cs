using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V1;
using Nocturne.API.Services;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Controllers;

/// <summary>
/// Unit tests for ActivityController
/// Tests the controller logic with mocked dependencies
/// </summary>
[Trait("Category", "Unit")]
public class ActivityControllerTests
{
    private readonly Mock<IStateSpanService> _mockStateSpanService;
    private readonly Mock<ILogger<ActivityController>> _mockLogger;
    private readonly ActivityController _controller;
    private readonly IDocumentProcessingService _documentProcessingService =
        new DocumentProcessingService(new Mock<ILogger<DocumentProcessingService>>().Object);

    public ActivityControllerTests()
    {
        _mockStateSpanService = new Mock<IStateSpanService>();
        _mockLogger = new Mock<ILogger<ActivityController>>();
        _controller = new ActivityController(
            _mockStateSpanService.Object,
            _documentProcessingService,
            _mockLogger.Object
        );

        // Set up HttpContext for the controller
        var httpContext = new DefaultHttpContext();
        _controller.ControllerContext = new ControllerContext() { HttpContext = httpContext };
    }

    [Fact]
    public async Task GetActivities_WhenActivitiesExist_ShouldReturnActivities()
    {
        // Arrange
        var expectedActivities = new List<Activity>
        {
            new Activity
            {
                Id = "507f1f77bcf86cd799439011",
                Type = "Exercise",
                Description = "Morning run",
                Duration = 30,
                Intensity = "Moderate",
                CreatedAt = "2024-01-01T10:00:00.000Z",
            },
            new Activity
            {
                Id = "507f1f77bcf86cd799439012",
                Type = "Walking",
                Description = "Evening walk",
                Duration = 20,
                Intensity = "Light",
                CreatedAt = "2024-01-01T18:00:00.000Z",
            },
        };

        _mockStateSpanService
            .Setup(x =>
                x.GetActivitiesAsync(It.IsAny<string?>(), 10, 0, It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(expectedActivities);

        // Act
        var result = await _controller.GetActivities(cancellationToken: CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Result.Should().BeOfType<OkObjectResult>();
        var okResult = result.Result as OkObjectResult;
        okResult!.Value.Should().BeEquivalentTo(expectedActivities);
    }

    [Fact]
    public async Task GetActivities_WhenNoActivitiesExist_ShouldReturnEmptyList()
    {
        // Arrange
        _mockStateSpanService
            .Setup(x =>
                x.GetActivitiesAsync(It.IsAny<string?>(), 10, 0, It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(new List<Activity>());

        // Act
        var result = await _controller.GetActivities(cancellationToken: CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Result.Should().BeOfType<OkObjectResult>();
        var okResult = result.Result as OkObjectResult;
        var activities = okResult!.Value as List<Activity>;
        activities.Should().BeEmpty();
    }

    [Fact]
    public async Task GetActivities_WithCustomParameters_ShouldPassParametersToService()
    {
        // Arrange
        var count = 5;
        var skip = 10;
        var expectedActivities = new List<Activity>();

        _mockStateSpanService
            .Setup(x =>
                x.GetActivitiesAsync(
                    It.IsAny<string?>(),
                    count,
                    skip,
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(expectedActivities);

        // Act
        await _controller.GetActivities(count, skip, CancellationToken.None);

        // Assert
        _mockStateSpanService.Verify(
            x =>
                x.GetActivitiesAsync(
                    It.IsAny<string?>(),
                    count,
                    skip,
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task GetActivity_WhenActivityExists_ShouldReturnActivity()
    {
        // Arrange
        var activityId = "507f1f77bcf86cd799439011";
        var expectedActivity = new Activity
        {
            Id = activityId,
            Type = "Exercise",
            Description = "Morning run",
            Duration = 30,
            Intensity = "Moderate",
            CreatedAt = "2024-01-01T10:00:00.000Z",
        };

        _mockStateSpanService
            .Setup(x => x.GetActivityByIdAsync(activityId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedActivity);

        // Act
        var result = await _controller.GetActivity(activityId, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Result.Should().BeOfType<OkObjectResult>();
        var okResult = result.Result as OkObjectResult;
        okResult!.Value.Should().BeEquivalentTo(expectedActivity);
    }

    [Fact]
    public async Task GetActivity_WhenActivityDoesNotExist_ShouldReturnNotFound()
    {
        // Arrange
        var activityId = "507f1f77bcf86cd799439011";

        _mockStateSpanService
            .Setup(x => x.GetActivityByIdAsync(activityId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Activity?)null);

        // Act
        var result = await _controller.GetActivity(activityId, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task CreateActivities_WithSingleActivity_ShouldCreateAndReturnActivity()
    {
        // Arrange
        var inputActivity = new Activity
        {
            Type = "Exercise",
            Description = "Morning run",
            Duration = 30,
            Intensity = "Moderate",
        };

        var createdActivity = new Activity
        {
            Id = "507f1f77bcf86cd799439011",
            Type = "Exercise",
            Description = "Morning run",
            Duration = 30,
            Intensity = "Moderate",
            CreatedAt = "2024-01-01T10:00:00.000Z",
        };

        var jsonElement = JsonSerializer.SerializeToElement(inputActivity);

        _mockStateSpanService
            .Setup(x =>
                x.CreateActivitiesAsync(
                    It.IsAny<IEnumerable<Activity>>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new List<Activity> { createdActivity });

        // Act
        var result = await _controller.CreateActivities(jsonElement, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Result.Should().BeOfType<OkObjectResult>();
        var okResult = result.Result as OkObjectResult;
        var returnedActivities = okResult!.Value as List<Activity>;
        returnedActivities.Should().ContainSingle();
        returnedActivities![0].Should().BeEquivalentTo(createdActivity);
    }

    [Fact]
    public async Task CreateActivities_WithMultipleActivities_ShouldCreateAndReturnActivities()
    {
        // Arrange
        var inputActivities = new List<Activity>
        {
            new Activity
            {
                Type = "Exercise",
                Description = "Morning run",
                Duration = 30,
            },
            new Activity
            {
                Type = "Walking",
                Description = "Evening walk",
                Duration = 20,
            },
        };

        var createdActivities = new List<Activity>
        {
            new Activity
            {
                Id = "507f1f77bcf86cd799439011",
                Type = "Exercise",
                Description = "Morning run",
                Duration = 30,
                CreatedAt = "2024-01-01T10:00:00.000Z",
            },
            new Activity
            {
                Id = "507f1f77bcf86cd799439012",
                Type = "Walking",
                Description = "Evening walk",
                Duration = 20,
                CreatedAt = "2024-01-01T10:01:00.000Z",
            },
        };

        var jsonElement = JsonSerializer.SerializeToElement(inputActivities);

        _mockStateSpanService
            .Setup(x =>
                x.CreateActivitiesAsync(
                    It.IsAny<IEnumerable<Activity>>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(createdActivities);

        // Act
        var result = await _controller.CreateActivities(jsonElement, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Result.Should().BeOfType<OkObjectResult>();
        var okResult = result.Result as OkObjectResult;
        var returnedActivities = okResult!.Value as List<Activity>;
        returnedActivities.Should().HaveCount(2);
        returnedActivities.Should().BeEquivalentTo(createdActivities);
    }

    [Fact]
    public async Task CreateActivities_WithNullData_ShouldReturnBadRequest()
    {
        // Arrange & Act
        var result = await _controller.CreateActivities(null!, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task UpdateActivity_WhenActivityExists_ShouldUpdateAndReturnActivity()
    {
        // Arrange
        var activityId = "507f1f77bcf86cd799439011";
        var inputActivity = new Activity
        {
            Type = "Exercise",
            Description = "Updated morning run",
            Duration = 45,
            Intensity = "High",
        };

        var updatedActivity = new Activity
        {
            Id = activityId,
            Type = "Exercise",
            Description = "Updated morning run",
            Duration = 45,
            Intensity = "High",
            CreatedAt = "2024-01-01T10:00:00.000Z",
        };

        _mockStateSpanService
            .Setup(x =>
                x.UpdateActivityAsync(activityId, inputActivity, It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(updatedActivity);

        // Act
        var result = await _controller.UpdateActivity(
            activityId,
            inputActivity,
            CancellationToken.None
        );

        // Assert
        result.Should().NotBeNull();
        result.Result.Should().BeOfType<OkObjectResult>();
        var okResult = result.Result as OkObjectResult;
        okResult!.Value.Should().BeEquivalentTo(updatedActivity);
    }

    [Fact]
    public async Task UpdateActivity_WhenActivityDoesNotExist_ShouldReturnNotFound()
    {
        // Arrange
        var activityId = "507f1f77bcf86cd799439011";
        var inputActivity = new Activity { Type = "Exercise", Description = "Test" };

        _mockStateSpanService
            .Setup(x =>
                x.UpdateActivityAsync(activityId, inputActivity, It.IsAny<CancellationToken>())
            )
            .ReturnsAsync((Activity?)null);

        // Act
        var result = await _controller.UpdateActivity(
            activityId,
            inputActivity,
            CancellationToken.None
        );

        // Assert
        result.Should().NotBeNull();
        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task UpdateActivity_WithNullData_ShouldReturnBadRequest()
    {
        // Arrange
        var activityId = "507f1f77bcf86cd799439011";

        // Act
        var result = await _controller.UpdateActivity(activityId, null!, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task DeleteActivity_WhenActivityExists_ShouldDeleteAndReturnSuccess()
    {
        // Arrange
        var activityId = "507f1f77bcf86cd799439011";

        _mockStateSpanService
            .Setup(x => x.DeleteActivityAsync(activityId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        var result = await _controller.DeleteActivity(activityId, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeOfType<OkObjectResult>();
        var okResult = result as OkObjectResult;
        var responseValue = okResult!.Value;
        responseValue.Should().NotBeNull();
    }

    [Fact]
    public async Task DeleteActivity_WhenActivityDoesNotExist_ShouldReturnNotFound()
    {
        // Arrange
        var activityId = "507f1f77bcf86cd799439011";

        _mockStateSpanService
            .Setup(x => x.DeleteActivityAsync(activityId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Act
        var result = await _controller.DeleteActivity(activityId, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task GetActivities_WhenServiceThrowsException_ShouldReturnInternalServerError()
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

        // Act
        var result = await _controller.GetActivities(cancellationToken: CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Result.Should().BeOfType<ObjectResult>();
        var objectResult = result.Result as ObjectResult;
        objectResult!.StatusCode.Should().Be(500);
    }

    [Fact]
    public async Task CreateActivities_WhenServiceThrowsException_ShouldReturnInternalServerError()
    {
        // Arrange
        var inputActivity = new Activity { Type = "Exercise", Description = "Test" };
        var jsonElement = JsonSerializer.SerializeToElement(inputActivity);

        _mockStateSpanService
            .Setup(x =>
                x.CreateActivitiesAsync(
                    It.IsAny<IEnumerable<Activity>>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ThrowsAsync(new Exception("Database error"));

        // Act
        var result = await _controller.CreateActivities(jsonElement, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Result.Should().BeOfType<ObjectResult>();
        var objectResult = result.Result as ObjectResult;
        objectResult!.StatusCode.Should().Be(500);
    }
}
