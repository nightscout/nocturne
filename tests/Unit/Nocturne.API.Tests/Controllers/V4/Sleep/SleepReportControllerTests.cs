using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Controllers.V4.Sleep;
using Nocturne.Core.Contracts.Sleep;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Sleep.Report;

namespace Nocturne.API.Tests.Controllers.V4.Sleep;

public class SleepReportControllerTests
{
    private static readonly DateTime From = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly Mock<ISleepReportService> _service = new();
    private readonly SleepReportController _controller;

    public SleepReportControllerTests()
    {
        _service
            .Setup(s => s.GetTrendsReportAsync(
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<SleepSource?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SleepTrendsReport());

        _controller = new SleepReportController(_service.Object);
    }

    [Fact]
    public async Task GetTrends_over_the_analytics_cap_returns_bad_request_without_building()
    {
        var result = await _controller.GetTrends(From, From.AddDays(V4ReadLimits.MaxAnalyticsSpanDays).AddMilliseconds(1));

        var objectResult = result.Result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(400);
        objectResult.Value.Should().BeOfType<ProblemDetails>()
            .Which.Detail.Should().Be($"Date range must not exceed {V4ReadLimits.MaxAnalyticsSpanDays} days.");
        _service.Verify(
            s => s.GetTrendsReportAsync(
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<SleepSource?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetTrends_at_the_analytics_cap_is_accepted()
    {
        var result = await _controller.GetTrends(From, From.AddDays(V4ReadLimits.MaxAnalyticsSpanDays));

        result.Result.Should().BeOfType<OkObjectResult>();
    }
}
