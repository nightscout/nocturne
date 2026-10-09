using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V4.Profiles;
using Nocturne.API.Tests.TestDoubles;
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
        controller.ProblemDetailsFactory = new EchoingProblemDetailsFactory();

        return controller;
    }

    private static ClockFaceConfig ConfigWith(string glucoseUnits, string timeFormat) => new()
    {
        Settings = new ClockSettings { GlucoseUnits = glucoseUnits, TimeFormat = timeFormat },
    };

    public static TheoryData<string, string, string> InvalidSettings => new()
    {
        { "mmol/L", "24", "settings.glucoseUnits" },
        { "", "24", "settings.glucoseUnits" },
        { "mmol", "24h", "settings.timeFormat" },
        { "mmol", "auto", "settings.timeFormat" },
    };

    [Theory]
    [MemberData(nameof(InvalidSettings))]
    public async Task Create_RejectsAnInvalidUnitOrTimeFormat(
        string glucoseUnits, string timeFormat, string field)
    {
        var result = await CreateController().Create(new CreateClockFaceRequest
        {
            Name = "Bedside",
            Config = ConfigWith(glucoseUnits, timeFormat),
        });

        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(400);
        ((ProblemDetails)problem.Value!).Detail.Should().StartWith(field);
        _clockFaceServiceMock.Verify(
            s => s.CreateAsync(It.IsAny<string>(), It.IsAny<CreateClockFaceRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Create_RejectsAConfigWithNoSettings()
    {
        var result = await CreateController().Create(new CreateClockFaceRequest
        {
            Name = "Bedside",
            Config = new ClockFaceConfig { Settings = null! },
        });

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
    }

    [Theory]
    [MemberData(nameof(InvalidSettings))]
    public async Task Update_RejectsAnInvalidUnitOrTimeFormat(
        string glucoseUnits, string timeFormat, string field)
    {
        var result = await CreateController().Update(Guid.NewGuid(), new UpdateClockFaceRequest
        {
            Config = ConfigWith(glucoseUnits, timeFormat),
        });

        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(400);
        ((ProblemDetails)problem.Value!).Detail.Should().StartWith(field);
        _clockFaceServiceMock.Verify(
            s => s.UpdateAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<UpdateClockFaceRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Update_AllowsARenameThatCarriesNoConfig()
    {
        var id = Guid.NewGuid();
        _clockFaceServiceMock
            .Setup(s => s.UpdateAsync(id, It.IsAny<string>(), It.IsAny<UpdateClockFaceRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClockFace { Id = id });

        var result = await CreateController().Update(id, new UpdateClockFaceRequest { Name = "Kitchen" });

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Create_SavesAFaceThatLeavesUnitsAndTimeFormatAtTheirDefaults()
    {
        _clockFaceServiceMock
            .Setup(s => s.CreateAsync(It.IsAny<string>(), It.IsAny<CreateClockFaceRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClockFace { Id = Guid.NewGuid() });

        var result = await CreateController().Create(new CreateClockFaceRequest
        {
            Name = "Bedside",
            Config = new ClockFaceConfig(),
        });

        result.Result.Should().BeOfType<CreatedAtActionResult>();
    }

    [Fact]
    public async Task Create_WithoutAConfig_LeavesTheStarterLayoutToTheService()
    {
        _clockFaceServiceMock
            .Setup(s => s.CreateAsync(It.IsAny<string>(), It.IsAny<CreateClockFaceRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClockFace { Id = Guid.NewGuid() });

        var result = await CreateController().Create(new CreateClockFaceRequest { Name = "Bedside" });

        result.Result.Should().BeOfType<CreatedAtActionResult>();
        _clockFaceServiceMock.Verify(
            s => s.CreateAsync(It.IsAny<string>(), It.Is<CreateClockFaceRequest>(r => r.Config == null), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Theory]
    [InlineData("mg/dl", "12")]
    [InlineData("mmol", "24")]
    public async Task Create_SavesAFaceWithValidUnitsAndTimeFormat(string glucoseUnits, string timeFormat)
    {
        _clockFaceServiceMock
            .Setup(s => s.CreateAsync(It.IsAny<string>(), It.IsAny<CreateClockFaceRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClockFace { Id = Guid.NewGuid() });

        var result = await CreateController().Create(new CreateClockFaceRequest
        {
            Name = "Bedside",
            Config = ConfigWith(glucoseUnits, timeFormat),
        });

        result.Result.Should().BeOfType<CreatedAtActionResult>();
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
