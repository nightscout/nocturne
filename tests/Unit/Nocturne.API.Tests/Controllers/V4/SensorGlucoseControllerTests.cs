using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V4.Glucose;
using Nocturne.API.Models.Requests.V4;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4;

[Trait("Category", "Unit")]
public class SensorGlucoseControllerTests
{
    private readonly Mock<ISensorGlucoseRepository> _repoMock = new();
    private readonly Mock<IGlucoseProcessingResolver> _glucoseResolverMock = new();
    private readonly Mock<IAlertOrchestrator> _alertOrchestratorMock = new();
    private readonly Mock<ILogger<SensorGlucoseController>> _loggerMock = new();

    private SensorGlucoseController CreateController()
    {
        var controller = new SensorGlucoseController(
            _repoMock.Object,
            _glucoseResolverMock.Object,
            _alertOrchestratorMock.Object,
            _loggerMock.Object);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };

        return controller;
    }

    [Fact]
    public async Task Create_Returns201_WhenSuccessful()
    {
        // Arrange
        var input = new UpsertSensorGlucoseRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            Mgdl = 120
        };

        var created = new SensorGlucose
        {
            Id = Guid.NewGuid(),
            Timestamp = input.Timestamp.UtcDateTime,
            Mgdl = 120
        };

        _repoMock
            .Setup(r => r.CreateAsync(It.IsAny<SensorGlucose>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(created);

        _repoMock.As<IV4Repository<SensorGlucose>>()
            .Setup(r => r.CreateAsync(It.IsAny<SensorGlucose>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(created);

        var controller = CreateController();

        // Act
        var result = await controller.Create(input);

        // Assert
        var createdResult = result.Result.Should().BeOfType<CreatedAtActionResult>().Subject;
        createdResult.StatusCode.Should().Be(StatusCodes.Status201Created);
        createdResult.Value.Should().Be(created);
    }
}
