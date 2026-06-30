using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V4.Glucose;
using Nocturne.API.Models.Requests.V4;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Xunit;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.API.Tests.Controllers.V4;

[Trait("Category", "Unit")]
public class SensorGlucoseControllerTests
{
    private readonly Mock<ISensorGlucoseRepository> _repoMock = new();
    private readonly Mock<IGlucoseProcessingResolver> _glucoseResolverMock = new();
    private readonly Mock<IAlertOrchestrator> _alertOrchestratorMock = new();
    private readonly Mock<IDataEventSink<SensorGlucose>> _eventsMock = new();
    private readonly Mock<ILogger<SensorGlucoseController>> _loggerMock = new();

    private SensorGlucoseController CreateController()
    {
        var controller = new SensorGlucoseController(
            _repoMock.Object,
            _glucoseResolverMock.Object,
            _alertOrchestratorMock.Object,
            _eventsMock.Object,
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
            .Setup(r => r.CreateAsync(It.IsAny<SensorGlucose>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(created);

        _repoMock.As<IV4Repository<SensorGlucose>>()
            .Setup(r => r.CreateAsync(It.IsAny<SensorGlucose>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(created);

        var controller = CreateController();

        // Act
        var result = await controller.Create(input);

        // Assert
        var createdResult = result.Result.Should().BeOfType<CreatedAtActionResult>().Subject;
        createdResult.StatusCode.Should().Be(StatusCodes.Status201Created);
        createdResult.Value.Should().Be(created);
    }

    [Fact]
    public async Task Create_BroadcastsRealtimeEvent_ForCreatedReading()
    {
        // Arrange
        var input = new UpsertSensorGlucoseRequest { Timestamp = DateTimeOffset.UtcNow, Mgdl = 120 };
        var created = new SensorGlucose { Id = Guid.NewGuid(), Timestamp = input.Timestamp.UtcDateTime, Mgdl = 120 };

        _repoMock
            .Setup(r => r.CreateAsync(It.IsAny<SensorGlucose>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(created);
        _repoMock.As<IV4Repository<SensorGlucose>>()
            .Setup(r => r.CreateAsync(It.IsAny<SensorGlucose>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(created);

        var controller = CreateController();

        // Act
        await controller.Create(input);

        // Assert — the V4 write must emit a real-time create so live dashboards update without a reload.
        _eventsMock.Verify(
            e => e.OnCreatedAsync(
                It.Is<IReadOnlyList<SensorGlucose>>(l => l.Count == 1 && l[0] == created),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CreateBulk_BroadcastsRealtimeEvent_ForAllCreatedReadings()
    {
        // Arrange
        var requests = new[]
        {
            new UpsertSensorGlucoseRequest { Timestamp = DateTimeOffset.UtcNow, Mgdl = 120 },
            new UpsertSensorGlucoseRequest { Timestamp = DateTimeOffset.UtcNow.AddMinutes(-5), Mgdl = 115 },
        };
        var created = new[]
        {
            new SensorGlucose { Id = Guid.NewGuid(), Timestamp = requests[0].Timestamp.UtcDateTime, Mgdl = 120 },
            new SensorGlucose { Id = Guid.NewGuid(), Timestamp = requests[1].Timestamp.UtcDateTime, Mgdl = 115 },
        };

        _repoMock
            .Setup(r => r.BulkCreateAsync(It.IsAny<IEnumerable<SensorGlucose>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(created);

        var controller = CreateController();

        // Act
        await controller.CreateSensorGlucoseBulk(requests);

        // Assert
        _eventsMock.Verify(
            e => e.OnCreatedAsync(
                It.Is<IReadOnlyList<SensorGlucose>>(l => l.Count == 2),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Update_BroadcastsRealtimeEvent_ForUpdatedReading()
    {
        // Arrange
        var id = Guid.NewGuid();
        var input = new UpsertSensorGlucoseRequest { Timestamp = DateTimeOffset.UtcNow, Mgdl = 95 };
        var existing = new SensorGlucose { Id = id, Timestamp = input.Timestamp.UtcDateTime, Mgdl = 120 };
        var updated = new SensorGlucose { Id = id, Timestamp = input.Timestamp.UtcDateTime, Mgdl = 95 };

        _repoMock
            .Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);
        _repoMock
            .Setup(r => r.UpdateAsync(id, It.IsAny<SensorGlucose>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(updated);

        var controller = CreateController();

        // Act
        await controller.Update(id, input);

        // Assert — an edited V4 reading must emit a real-time update so live dashboards reflect the change.
        _eventsMock.Verify(
            e => e.OnUpdatedAsync(updated, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Restore_BroadcastsRealtimeEvent_ForRestoredReading()
    {
        // Arrange
        var id = Guid.NewGuid();
        var restored = new SensorGlucose { Id = id, Timestamp = DateTime.UtcNow, Mgdl = 110 };

        _repoMock
            .Setup(r => r.RestoreAsync(id, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(restored);

        var controller = CreateController();

        // Act
        await controller.Restore(id);

        // Assert — a restored reading reappears, so it must surface as a real-time create.
        _eventsMock.Verify(
            e => e.OnCreatedAsync(
                It.Is<IReadOnlyList<SensorGlucose>>(l => l.Count == 1 && l[0] == restored),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
