using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V1;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Controllers;

/// <summary>
/// Unit tests for ActivityController
/// Tests the controller logic with mocked dependencies
/// </summary>
[Trait("Category", "Unit")]
public class ActivityControllerTests
{
    private readonly Mock<IActivityService> _mockActivityService;
    private readonly Mock<IActivityDecomposer> _mockActivityDecomposer;
    private readonly Mock<ILogger<ActivityController>> _mockLogger;
    private readonly ActivityController _controller;

    public ActivityControllerTests()
    {
        _mockActivityService = new Mock<IActivityService>();
        _mockActivityDecomposer = new Mock<IActivityDecomposer>();
        _mockLogger = new Mock<ILogger<ActivityController>>();
        _controller = new ActivityController(
            _mockActivityService.Object,
            _mockActivityDecomposer.Object,
            _mockLogger.Object
        );

        // Set up HttpContext for the controller
        var httpContext = new DefaultHttpContext();
        _controller.ControllerContext = new ControllerContext() { HttpContext = httpContext };

        // Default read classification: a regular StateSpan-backed activity, read under treatments.
        // Tests that exercise a dedicated category override this per record.
        _mockActivityDecomposer
            .Setup(d => d.RequiredReadScope(It.IsAny<Activity>()))
            .Returns(OAuthScopes.TreatmentsRead);
        GrantScopes(OAuthScopes.TreatmentsRead);
    }

    /// <summary>Populates the request's granted scopes, as the auth middleware does at runtime.</summary>
    private void GrantScopes(params string[] scopes) =>
        _controller.HttpContext.Items["GrantedScopes"] = (IReadOnlySet<string>)new HashSet<string>(scopes);

    /// <summary>
    /// Classifies each activity's read scope by its <see cref="Activity.Type"/>, standing in for the
    /// real decomposer's routing.
    /// </summary>
    private void ClassifyReadScopesByType(Dictionary<string, string> typeToReadScope) =>
        _mockActivityDecomposer
            .Setup(d => d.RequiredReadScope(It.IsAny<Activity>()))
            .Returns((Activity a) => typeToReadScope.GetValueOrDefault(a.Type ?? "", OAuthScopes.TreatmentsRead));

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

        _mockActivityService
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
        _mockActivityService
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

        _mockActivityService
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
        _mockActivityService.Verify(
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

        _mockActivityService
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

        _mockActivityService
            .Setup(x => x.GetActivityByIdAsync(activityId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Activity?)null);

        // Act
        var result = await _controller.GetActivity(activityId, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    /// <summary>
    /// The merged activity read serves four storages. A caller holding one category must see that
    /// category and nothing else, which is the cross-category read the per-action scope gate on its
    /// own could not close.
    /// </summary>
    [Fact]
    public async Task GetActivities_HeartRateOnlyGrant_ReturnsOnlyHeartRateRecords()
    {
        var merged = new List<Activity>
        {
            new() { Id = "hr", Type = "HeartRate", Mills = 4 },
            new() { Id = "sc", Type = "StepCount", Mills = 3 },
            new() { Id = "sleep", Type = "Sleep", Mills = 2 },
            new() { Id = "ex", Type = "Exercise", Mills = 1 },
        };
        ClassifyReadScopesByType(new()
        {
            ["HeartRate"] = OAuthScopes.HeartRateRead,
            ["StepCount"] = OAuthScopes.StepCountRead,
            ["Sleep"] = OAuthScopes.SleepRead,
            ["Exercise"] = OAuthScopes.TreatmentsRead,
        });
        GrantScopes(OAuthScopes.HeartRateRead);
        _mockActivityService
            .Setup(x => x.GetActivitiesAsync(It.IsAny<string?>(), 10, 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(merged);

        var result = await _controller.GetActivities(cancellationToken: CancellationToken.None);

        var returned = (result.Result as OkObjectResult)!.Value as List<Activity>;
        returned!.Select(a => a.Id).Should().Equal("hr");
    }

    [Fact]
    public async Task GetActivities_TreatmentsOnlyGrant_DropsHeartRateStepsAndSleep()
    {
        var merged = new List<Activity>
        {
            new() { Id = "hr", Type = "HeartRate", Mills = 4 },
            new() { Id = "sc", Type = "StepCount", Mills = 3 },
            new() { Id = "sleep", Type = "Sleep", Mills = 2 },
            new() { Id = "ex", Type = "Exercise", Mills = 1 },
        };
        ClassifyReadScopesByType(new()
        {
            ["HeartRate"] = OAuthScopes.HeartRateRead,
            ["StepCount"] = OAuthScopes.StepCountRead,
            ["Sleep"] = OAuthScopes.SleepRead,
            ["Exercise"] = OAuthScopes.TreatmentsRead,
        });
        GrantScopes(OAuthScopes.TreatmentsRead);
        _mockActivityService
            .Setup(x => x.GetActivitiesAsync(It.IsAny<string?>(), 10, 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(merged);

        var result = await _controller.GetActivities(cancellationToken: CancellationToken.None);

        var returned = (result.Result as OkObjectResult)!.Value as List<Activity>;
        returned!.Select(a => a.Id).Should().Equal("ex");
    }

    /// <summary>
    /// A record whose category the caller does not hold answers 404, the same as a record that does
    /// not exist, so the response does not disclose the record.
    /// </summary>
    [Fact]
    public async Task GetActivity_RecordInUnheldCategory_ReturnsNotFound()
    {
        var activityId = "hr-1";
        ClassifyReadScopesByType(new() { ["HeartRate"] = OAuthScopes.HeartRateRead });
        GrantScopes(OAuthScopes.TreatmentsRead);
        _mockActivityService
            .Setup(x => x.GetActivityByIdAsync(activityId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Activity { Id = activityId, Type = "HeartRate", Mills = 1 });

        var result = await _controller.GetActivity(activityId, CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task GetActivity_RecordInHeldCategory_IsReturned()
    {
        var activityId = "hr-1";
        var record = new Activity { Id = activityId, Type = "HeartRate", Mills = 1 };
        ClassifyReadScopesByType(new() { ["HeartRate"] = OAuthScopes.HeartRateRead });
        GrantScopes(OAuthScopes.HeartRateRead);
        _mockActivityService
            .Setup(x => x.GetActivityByIdAsync(activityId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(record);

        var result = await _controller.GetActivity(activityId, CancellationToken.None);

        (result.Result as OkObjectResult)!.Value.Should().BeEquivalentTo(record);
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

        _mockActivityService
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
    public async Task CreateActivities_SleepRecordWithoutSleepScope_ReturnsForbidden()
    {
        // A caller holding only treatments.readwrite (enough to pass the endpoint's RequireScope)
        // must still be blocked from writing sleep data through the merged activity endpoint.
        GrantScopes(OAuthScopes.TreatmentsReadWrite);
        _mockActivityDecomposer
            .Setup(d => d.RequiredWriteScope(It.IsAny<Activity>()))
            .Returns(OAuthScopes.SleepReadWrite);

        var jsonElement = JsonSerializer.SerializeToElement(
            new Activity { Type = "sleep", Duration = 480 });

        var result = await _controller.CreateActivities(jsonElement, CancellationToken.None);

        var objectResult = result.Result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        _mockActivityService.Verify(
            x => x.CreateActivitiesAsync(It.IsAny<IEnumerable<Activity>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CreateActivities_SleepRecordWithSleepScope_Proceeds()
    {
        GrantScopes(OAuthScopes.SleepReadWrite);
        _mockActivityDecomposer
            .Setup(d => d.RequiredWriteScope(It.IsAny<Activity>()))
            .Returns(OAuthScopes.SleepReadWrite);
        _mockActivityService
            .Setup(x => x.CreateActivitiesAsync(It.IsAny<IEnumerable<Activity>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Activity> { new() { Id = "s1", Type = "sleep" } });

        var jsonElement = JsonSerializer.SerializeToElement(
            new Activity { Type = "sleep", Duration = 480 });

        var result = await _controller.CreateActivities(jsonElement, CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        _mockActivityService.Verify(
            x => x.CreateActivitiesAsync(It.IsAny<IEnumerable<Activity>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task UpdateActivity_ExistingRecordIsSleep_WithoutSleepScope_ReturnsForbidden()
    {
        // The id addresses an existing sleep session; even an exercise-typed payload edits sleep
        // data, so a caller lacking sleep.readwrite must be blocked on the existing-record branch.
        const string id = "sleep-session-1";
        GrantScopes(OAuthScopes.TreatmentsReadWrite);
        _mockActivityService
            .Setup(x => x.GetActivityByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Activity { Id = id, Type = "sleep" });
        _mockActivityDecomposer
            .Setup(d => d.RequiredWriteScope(It.Is<Activity>(a => a.Type == "sleep")))
            .Returns(OAuthScopes.SleepReadWrite);

        var result = await _controller.UpdateActivity(
            id, new Activity { Type = "exercise" }, CancellationToken.None);

        var objectResult = result.Result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        _mockActivityService.Verify(
            x => x.UpdateActivityAsync(It.IsAny<string>(), It.IsAny<Activity>(), It.IsAny<CancellationToken>()),
            Times.Never);
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

        _mockActivityService
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

        _mockActivityService
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

        _mockActivityService
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

        _mockActivityService
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

        _mockActivityService
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
        _mockActivityService
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

        _mockActivityService
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
