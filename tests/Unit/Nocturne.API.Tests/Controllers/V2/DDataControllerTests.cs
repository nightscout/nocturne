using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Template;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V2;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V2;

/// <summary>
/// Tests for V2 DDataController with 1:1 legacy compatibility
/// </summary>
public class DDataControllerTests
{
    private readonly Mock<IDDataService> _mockDDataService;
    private readonly Mock<IActivityDecomposer> _mockDecomposer;
    private readonly Mock<ILogger<DDataController>> _mockLogger;
    private readonly DDataController _controller;

    public DDataControllerTests()
    {
        _mockDDataService = new Mock<IDDataService>();
        _mockDecomposer = new Mock<IActivityDecomposer>();
        _mockLogger = new Mock<ILogger<DDataController>>();
        _controller = new DDataController(
            _mockDDataService.Object,
            _mockDecomposer.Object,
            _mockLogger.Object
        );
        Grant();
    }

    private void Grant(params string[] scopes)
    {
        var context = new DefaultHttpContext();
        context.Items[AuthContextKeys.GrantedScopes] = new HashSet<string>(scopes);
        _controller.ControllerContext = new ControllerContext { HttpContext = context };
    }

    [Theory]
    [InlineData("/at")]
    [InlineData("/at/2022-01-01T00:00:00.000Z")]
    public void GetDDataAt_RouteTemplate_MatchesWithAndWithoutTimestamp(string path)
    {
        var template = typeof(DDataController)
            .GetMethod(nameof(DDataController.GetDDataAt))!
            .GetCustomAttributes(typeof(HttpGetAttribute), false)
            .Cast<HttpGetAttribute>()
            .Single()
            .Template!;
        var matcher = new TemplateMatcher(TemplateParser.Parse(template), new RouteValueDictionary());

        Assert.True(matcher.TryMatch(path, new RouteValueDictionary()));
    }

    [Fact]
    public async Task GetDDataAt_ActivityOfCategoriesWithoutReadScope_IsDropped()
    {
        var heart = new Activity { Id = "heart" };
        var steps = new Activity { Id = "steps" };
        _mockDecomposer
            .Setup(x => x.RequiredReadScope(It.IsAny<Activity>()))
            .Returns((Activity a) => a.Id == "heart" ? Scope.HeartRateRead : Scope.StepCountRead);
        _mockDDataService
            .Setup(x => x.GetDDataFrameAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DDataFrame { Activity = [heart, steps] });
        Grant(Scope.GlucoseRead, Scope.HeartRateRead);

        var result = await _controller.GetDDataAt(cancellationToken: CancellationToken.None);

        var response = Assert.IsType<DDataFrame>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(["heart"], response.Activity.Select(a => a.Id));
    }

    [Fact]
    public async Task GetDData_ShouldReturnOkResult()
    {
        // Arrange
        var expectedResponse = new DDataResponse
        {
            Sgvs = new List<Entry>(),
            Treatments = new List<Treatment>(),
            DeviceStatus = new List<DeviceStatus>(),
            Profiles = new List<Profile>(),
            Mbgs = new List<Entry>(),
            Cals = new List<Entry>(),
            Food = new List<Food>(),
            DbStats = new DbStats(),
        };

        _mockDDataService
            .Setup(x =>
                x.GetDDataWithRecentStatusesAsync(It.IsAny<long>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(expectedResponse);

        // Act
        var result = await _controller.GetDData(CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<DDataResponse>(okResult.Value);
        Assert.NotNull(response);
        Assert.NotNull(response.Sgvs);
        Assert.NotNull(response.Treatments);
    }

    [Fact]
    public async Task GetDDataAt_WithValidTimestamp_ShouldReturnOkResult()
    {
        // Arrange
        var timestamp = "1640995200000"; // Valid Unix timestamp
        var expectedResponse = new DDataFrame { LastUpdated = 1640995200000 };

        _mockDDataService
            .Setup(x =>
                x.GetDDataFrameAsync(It.IsAny<long>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(expectedResponse);

        // Act
        var result = await _controller.GetDDataAt(timestamp, CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<DDataFrame>(okResult.Value);
        Assert.Equal(1640995200000, response.LastUpdated);
    }

    [Fact]
    public async Task GetDDataAt_WithValidIsoDate_ShouldReturnOkResult()
    {
        // Arrange
        var timestamp = "2022-01-01T00:00:00.000Z"; // Valid ISO date
        var expectedResponse = new DDataFrame { LastUpdated = 1640995200000 };

        _mockDDataService
            .Setup(x =>
                x.GetDDataFrameAsync(It.IsAny<long>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(expectedResponse);

        // Act
        var result = await _controller.GetDDataAt(timestamp, CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<DDataFrame>(okResult.Value);
        Assert.Equal(1640995200000, response.LastUpdated);
    }

    [Fact]
    public async Task GetRawDData_ActivityOfCategoriesWithoutReadScope_IsDropped()
    {
        var heart = new Activity { Id = "heart" };
        var steps = new Activity { Id = "steps" };
        _mockDecomposer
            .Setup(x => x.RequiredReadScope(It.IsAny<Activity>()))
            .Returns((Activity a) => a.Id == "heart" ? Scope.HeartRateRead : Scope.StepCountRead);
        _mockDDataService
            .Setup(x => x.GetDDataAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DData { Activity = [heart, steps] });
        Grant(Scope.GlucoseRead, Scope.StepCountRead);

        var result = await _controller.GetRawDData(cancellationToken: CancellationToken.None);

        var response = Assert.IsType<DData>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(["steps"], response.Activity.Select(a => a.Id));
    }

    [Fact]
    public async Task GetDDataAt_WithoutTimestamp_QueriesNow()
    {
        _mockDDataService
            .Setup(x => x.GetDDataFrameAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DDataFrame());
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var result = await _controller.GetDDataAt(cancellationToken: CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        _mockDDataService.Verify(x =>
            x.GetDDataFrameAsync(It.Is<long>(t => t >= before), It.IsAny<CancellationToken>())
        );
        _mockDDataService.Verify(
            x => x.GetDDataWithRecentStatusesAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
    }

    [Fact]
    public async Task GetDDataAt_WithInvalidTimestamp_ShouldReturnBadRequest()
    {
        // Arrange
        var invalidTimestamp = "invalid-timestamp";

        // Act
        var result = await _controller.GetDDataAt(invalidTimestamp, CancellationToken.None);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.NotNull(badRequestResult.Value);
    }

    [Fact]
    public async Task GetRawDData_WithoutTimestamp_ShouldReturnCurrentData()
    {
        // Arrange
        var expectedData = new DData
        {
            LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Sgvs = new List<Entry>(),
            Treatments = new List<Treatment>(),
        };

        _mockDDataService
            .Setup(x => x.GetDDataAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedData);

        // Act
        var result = await _controller.GetRawDData(cancellationToken: CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<DData>(okResult.Value);
        Assert.NotNull(response);
        Assert.True(response.LastUpdated > 0);
    }

    [Fact]
    public async Task GetRawDData_WithTimestamp_ShouldReturnDataForTimestamp()
    {
        // Arrange
        var timestamp = "1640995200000";
        var expectedData = new DData { LastUpdated = 1640995200000 };

        _mockDDataService
            .Setup(x => x.GetDDataAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedData);

        // Act
        var result = await _controller.GetRawDData(timestamp, CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<DData>(okResult.Value);
        Assert.NotNull(response);
    }

    [Fact]
    public async Task GetDData_WhenServiceThrows_ShouldReturn500()
    {
        // Arrange
        _mockDDataService
            .Setup(x =>
                x.GetDDataWithRecentStatusesAsync(It.IsAny<long>(), It.IsAny<CancellationToken>())
            )
            .ThrowsAsync(new Exception("Test exception"));

        // Act
        var result = await _controller.GetDData(CancellationToken.None);

        // Assert
        var statusResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(500, statusResult.StatusCode);
    }
}
