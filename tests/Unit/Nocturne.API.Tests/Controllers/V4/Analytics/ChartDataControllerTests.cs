using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Controllers.V4.Analytics;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Models;

namespace Nocturne.API.Tests.Controllers.V4.Analytics;

public class ChartDataControllerTests
{
    private const long Start = 1_767_225_600_000;
    private const long AtCap = Start + V4ReadLimits.MaxDateSpanDays * TimeSpan.MillisecondsPerDay;

    private readonly Mock<IChartDataService> _service = new();
    private readonly ChartDataController _controller;

    public ChartDataControllerTests()
    {
        _service
            .Setup(s => s.GetDashboardChartDataAsync(
                It.IsAny<long>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DashboardChartData());
        _service
            .Setup(s => s.GetBasalSeriesAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        _controller = new ChartDataController(_service.Object, NullLogger<ChartDataController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    [Fact]
    public async Task GetDashboardChartData_over_the_cap_returns_bad_request_without_computing()
    {
        var result = await _controller.GetDashboardChartData(Start, AtCap + 1);

        OverCapDetail(result.Result).Should().Be($"Date range must not exceed {V4ReadLimits.MaxDateSpanDays} days.");
        _service.Verify(
            s => s.GetDashboardChartDataAsync(
                It.IsAny<long>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetDashboardChartData_at_the_cap_is_accepted()
    {
        var result = await _controller.GetDashboardChartData(Start, AtCap);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetBasalSeries_over_the_cap_returns_bad_request_without_computing()
    {
        var result = await _controller.GetBasalSeries(Start, AtCap + 1);

        OverCapDetail(result.Result).Should().Be($"Date range must not exceed {V4ReadLimits.MaxDateSpanDays} days.");
        _service.Verify(
            s => s.GetBasalSeriesAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetBasalSeries_at_the_cap_is_accepted()
    {
        var result = await _controller.GetBasalSeries(Start, AtCap);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    private static string? OverCapDetail(ActionResult? result)
    {
        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(400);
        return objectResult.Value.Should().BeOfType<ProblemDetails>().Subject.Detail;
    }
}
