using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Configuration;
using Nocturne.API.Controllers.V1;
using Nocturne.API.Services.Devices;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Xunit;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Tests.Controllers.V1;

/// <summary>
/// Unit tests for CountController verifying IEntryStore integration.
/// </summary>
[Trait("Category", "Unit")]
public class CountControllerTests
{
    private readonly Mock<IEntryStore> _mockEntryStore;
    private readonly Mock<ITreatmentStore> _mockTreatmentStore;
    private readonly Mock<IApsSnapshotRepository> _mockApsSnapshotRepository;
    private readonly Mock<IPumpSnapshotRepository> _mockPumpSnapshotRepository;
    private readonly Mock<IProfileProjectionService> _mockProfileProjectionService;
    private readonly Mock<IFoodRepository> _mockFoodRepository;
    private readonly Mock<IActivityService> _mockActivityService;
    private readonly Mock<ILogger<CountController>> _mockLogger;
    private readonly CountController _controller;

    public CountControllerTests()
    {
        _mockEntryStore = new Mock<IEntryStore>();
        _mockTreatmentStore = new Mock<ITreatmentStore>();
        _mockApsSnapshotRepository = new Mock<IApsSnapshotRepository>();
        _mockPumpSnapshotRepository = new Mock<IPumpSnapshotRepository>();
        _mockProfileProjectionService = new Mock<IProfileProjectionService>();
        _mockFoodRepository = new Mock<IFoodRepository>();
        _mockActivityService = new Mock<IActivityService>();
        _mockLogger = new Mock<ILogger<CountController>>();

        _controller = new CountController(
            _mockEntryStore.Object,
            _mockTreatmentStore.Object,
            DeviceStatusProjection(_mockApsSnapshotRepository, _mockPumpSnapshotRepository),
            _mockProfileProjectionService.Object,
            _mockFoodRepository.Object,
            _mockActivityService.Object,
            TimeProvider.System,
            _mockLogger.Object
        );

        // CountGeneric resolves the required scope from the storage selector, so these delegation
        // tests need a scope set. Full access keeps them about delegation;
        // CountGeneric_RefusesAStorageOutsideTheGrant covers the refusal.
        var httpContext = new DefaultHttpContext();
        httpContext.Items["GrantedScopes"] = Scope.Normalize([Scope.FullAccess]);

        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
    }

    [Fact]
    public async Task CountGeneric_RefusesAStorageOutsideTheGrant()
    {
        // The route serves five collections, so an attribute could only OR across them and would
        // let a glucose-only grant learn a treatment row count.
        var httpContext = new DefaultHttpContext();
        httpContext.Items["GrantedScopes"] = Scope.Normalize([Scope.GlucoseRead]);
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var refused = await _controller.CountGeneric("treatments");

        refused.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        _mockTreatmentStore.Verify(
            s => s.CountAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);

        var allowed = await _controller.CountGeneric("entries");
        allowed.Result.Should().NotBeOfType<ObjectResult>();
    }

    [Fact]
    public async Task CountEntries_DelegatesToEntryStore()
    {
        // Arrange
        var find = "{\"type\":\"sgv\"}";
        var type = "sgv";
        _mockEntryStore
            .Setup(s => s.CountAsync(find, type, It.IsAny<CancellationToken>()))
            .ReturnsAsync(42L);

        // Act
        var result = await _controller.CountEntries(find, type);

        // Assert
        Rows(result).Should().ContainSingle().Which.Count.Should().Be(42L);

        _mockEntryStore.Verify(
            s => s.CountAsync(find, type, It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    public async Task CountGeneric_Entries_DelegatesToEntryStore()
    {
        // Arrange
        var find = "{\"dateString\":{\"$gte\":\"2024-01-01\"}}";
        var type = "mbg";
        _mockEntryStore
            .Setup(s => s.CountAsync(find, type, It.IsAny<CancellationToken>()))
            .ReturnsAsync(7L);

        // Act
        var result = await _controller.CountGeneric("entries", find, type);

        // Assert
        Rows(result).Should().ContainSingle().Which.Count.Should().Be(7L);

        _mockEntryStore.Verify(
            s => s.CountAsync(find, type, It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Theory]
    [InlineData("entries")]
    [InlineData("treatments")]
    [InlineData("devicestatus")]
    [InlineData("profile")]
    [InlineData("food")]
    [InlineData("activity")]
    public async Task CountGeneric_NoMatch_AnswersNoRow(string storage)
    {
        var result = await _controller.CountGeneric(storage);

        Rows(result).Should().BeEmpty();
    }

    [Fact]
    public async Task CountEvery_NoMatch_AnswersNoRow()
    {
        Rows(await _controller.CountEntries()).Should().BeEmpty();
        Rows(await _controller.CountTreatments()).Should().BeEmpty();
        Rows(await _controller.CountDeviceStatus()).Should().BeEmpty();
        Rows(await _controller.CountActivity()).Should().BeEmpty();
    }

    [Fact]
    public async Task CountEvery_Match_AnswersOneGroupRow()
    {
        _mockTreatmentStore
            .Setup(s => s.CountAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(3L);
        _mockApsSnapshotRepository
            .Setup(s => s.CountAsync(null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);
        _mockPumpSnapshotRepository
            .Setup(s => s.CountUncorrelatedAsync(null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        _mockActivityService
            .Setup(s => s.CountActivitiesAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(5L);
        _mockProfileProjectionService
            .Setup(s => s.CountProfilesAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(6L);
        _mockFoodRepository
            .Setup(s => s.CountFoodAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(8L);

        Rows(await _controller.CountTreatments()).Should().ContainSingle().Which.Count.Should().Be(3L);
        Rows(await _controller.CountDeviceStatus()).Should().ContainSingle().Which.Count.Should().Be(4L);
        Rows(await _controller.CountActivity()).Should().ContainSingle().Which.Count.Should().Be(5L);
        Rows(await _controller.CountGeneric("treatments")).Should().ContainSingle().Which.Count.Should().Be(3L);
        Rows(await _controller.CountGeneric("devicestatus")).Should().ContainSingle().Which.Count.Should().Be(4L);
        Rows(await _controller.CountGeneric("activity")).Should().ContainSingle().Which.Count.Should().Be(5L);
        Rows(await _controller.CountGeneric("profile")).Should().ContainSingle().Which.Count.Should().Be(6L);
        Rows(await _controller.CountGeneric("food")).Should().ContainSingle().Which.Count.Should().Be(8L);
    }

    [Fact]
    public async Task CountEntries_BracketedFind_ReachesTheStore()
    {
        const string queryString = "find[type]=sgv&find[sgv][$gte]=100";
        _controller.ControllerContext.HttpContext.Request.QueryString = new QueryString("?" + queryString);
        _mockEntryStore
            .Setup(s => s.CountAsync(queryString, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(2L);

        var result = await _controller.CountEntries(find: null);

        Rows(result).Should().ContainSingle().Which.Count.Should().Be(2L);
    }

    [Fact]
    public async Task CountGeneric_Entries_BracketedFind_ReachesTheStore()
    {
        const string queryString = "find%5Btype%5D=mbg";
        _controller.ControllerContext.HttpContext.Request.QueryString = new QueryString("?" + queryString);
        _mockEntryStore
            .Setup(s => s.CountAsync(queryString, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1L);

        var result = await _controller.CountGeneric("entries", find: null);

        Rows(result).Should().ContainSingle().Which.Count.Should().Be(1L);
    }

    [Fact]
    public void LegacyCountResult_SerializesAsTheAggregateRow()
    {
        var options = NightscoutJsonOptions.Create();

        JsonSerializer.Serialize(new[] { new LegacyCountResult { Count = 1 } }, options)
            .Should().Be("""[{"_id":null,"count":1}]""");
        JsonSerializer.Serialize(Array.Empty<LegacyCountResult>(), options).Should().Be("[]");
    }

    [Fact]
    public async Task CountEntries_StoreFailure_Answers500()
    {
        _mockEntryStore
            .Setup(s => s.CountAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("synthetic"));

        (await _controller.CountEntries()).Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        (await _controller.CountGeneric("entries")).Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
    }

    [Fact]
    public async Task CountDeviceStatus_BracketedFind_BoundsTheCount()
    {
        _controller.ControllerContext.HttpContext.Request.QueryString =
            new QueryString("?find[created_at][$gte]=2026-01-01T00:00:00Z&find[created_at][$lt]=2026-01-02T00:00:00Z");
        var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        _mockApsSnapshotRepository
            .Setup(s => s.CountAsync(
                It.Is<DateTime?>(d => d == from), It.Is<DateTime?>(d => d == to), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(2);

        Rows(await _controller.CountDeviceStatus(find: null)).Should().ContainSingle().Which.Count.Should().Be(2L);
        Rows(await _controller.CountGeneric("devicestatus", find: null)).Should().ContainSingle().Which.Count.Should().Be(2L);
    }

    [Fact]
    public async Task CountDeviceStatus_JsonFind_BoundsTheCount()
    {
        var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        _mockApsSnapshotRepository
            .Setup(s => s.CountAsync(It.Is<DateTime?>(d => d == from), null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var result = await _controller.CountDeviceStatus("{\"created_at\":{\"$gte\":\"2026-01-01T00:00:00Z\"}}");

        Rows(result).Should().ContainSingle().Which.Count.Should().Be(1L);
    }

    internal static DeviceStatusProjectionService DeviceStatusProjection(
        Mock<IApsSnapshotRepository> aps, Mock<IPumpSnapshotRepository> pump) =>
        new(aps.Object, pump.Object, Mock.Of<IUploaderSnapshotRepository>(), Mock.Of<IStateSpanRepository>(),
            Mock.Of<IDeviceStatusExtrasRepository>(), NullLogger<DeviceStatusProjectionService>.Instance);

    private static LegacyCountResult[] Rows(ActionResult<LegacyCountResult[]> result) =>
        result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeAssignableTo<LegacyCountResult[]>().Subject;
}
