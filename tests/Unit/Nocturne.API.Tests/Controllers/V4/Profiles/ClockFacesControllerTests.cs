using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V4.Profiles;
using Nocturne.Core.Contracts.Platform;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Profiles;

[Trait("Category", "Unit")]
public class ClockFacesControllerTests
{
    private readonly Mock<IClockFaceService> _clockFaceServiceMock = new();
    private readonly Mock<ISensorGlucoseRepository> _sensorGlucoseRepositoryMock = new();
    private readonly Mock<ILogger<ClockFacesController>> _loggerMock = new();

    private ClockFacesController CreateController()
    {
        var controller = new ClockFacesController(
            _clockFaceServiceMock.Object,
            _sensorGlucoseRepositoryMock.Object,
            _loggerMock.Object);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };

        return controller;
    }

    [Fact]
    public async Task GetGlucose_ReturnsNotFound_AndNeverReadsGlucose_WhenClockDoesNotExist()
    {
        var id = Guid.NewGuid();
        _clockFaceServiceMock
            .Setup(s => s.GetByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ClockFace?)null);

        var result = await CreateController().GetGlucose(id);

        result.Result.Should().BeOfType<NotFoundResult>();

        // The clock UUID is the capability: with no valid clock, no glucose is ever read.
        _sensorGlucoseRepositoryMock.Verify(
            r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetGlucose_ReturnsLatestReadingsMappedToClockDto_WhenClockExists()
    {
        var id = Guid.NewGuid();
        _clockFaceServiceMock
            .Setup(s => s.GetByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClockFace { Id = id, Config = new ClockFaceConfig() });

        var newest = new SensorGlucose
        {
            Timestamp = new DateTime(2026, 6, 11, 12, 5, 0, DateTimeKind.Utc),
            Mgdl = 120,
            Direction = GlucoseDirection.Flat,
            Delta = 3,
            DataSource = "dexcom",
        };
        var previous = new SensorGlucose
        {
            Timestamp = new DateTime(2026, 6, 11, 12, 0, 0, DateTimeKind.Utc),
            Mgdl = 117,
            Direction = GlucoseDirection.FortyFiveUp,
            Delta = 2,
            DataSource = "dexcom",
        };

        _sensorGlucoseRepositoryMock
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { newest, previous });

        var result = await CreateController().GetGlucose(id);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var dtos = ok.Value.Should().BeAssignableTo<ClockGlucoseDto[]>().Subject;

        dtos.Should().HaveCount(2);
        dtos[0].Mills.Should().Be(newest.Mills);
        dtos[0].Mgdl.Should().Be(120);
        dtos[0].Direction.Should().Be("Flat");
        dtos[0].Delta.Should().Be(3);
        dtos[0].DataSource.Should().Be("dexcom");
        dtos[1].Direction.Should().Be("FortyFiveUp");
    }

    [Fact]
    public async Task GetGlucose_RequestsOnlyTheLatestTwoReadings_NewestFirst()
    {
        var id = Guid.NewGuid();
        _clockFaceServiceMock
            .Setup(s => s.GetByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClockFace { Id = id, Config = new ClockFaceConfig() });
        _sensorGlucoseRepositoryMock
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SensorGlucose>());

        await CreateController().GetGlucose(id);

        _sensorGlucoseRepositoryMock.Verify(
            r => r.GetAsync(
                null, null, null, null,
                2, 0, true, It.IsAny<bool>(),
                It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
