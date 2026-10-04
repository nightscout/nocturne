using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V4.Analytics;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Models.Services;

namespace Nocturne.API.Tests.Controllers.V4.Analytics;

public class DataOverviewControllerTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task YearSummary_PartialFailureIsNotCached(bool dailyFails)
    {
        var service = new Mock<IDataOverviewService>();
        service.Setup(source => source.GetYearSummaryAsync(2024, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new YearSummaryResponse
            {
                DailySummary = dailyFails ? null : new DailySummaryResponse(),
                GriTimeline = dailyFails ? new GriTimelineResponse() : null,
            });
        var controller = new DataOverviewController(service.Object, Mock.Of<ILogger<DataOverviewController>>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        var result = await controller.GetYearSummary(2024);

        result.Result.Should().BeOfType<OkObjectResult>();
        controller.Response.Headers.CacheControl.ToString().Should().Be("no-store");
    }

    [Theory]
    [InlineData(1969)]
    [InlineData(2101)]
    public async Task YearSummary_RejectsInvalidYearsBeforeReading(int year)
    {
        var service = new Mock<IDataOverviewService>(MockBehavior.Strict);
        var controller = new DataOverviewController(service.Object, Mock.Of<ILogger<DataOverviewController>>());

        var result = await controller.GetYearSummary(year);

        result.Result.Should().BeOfType<BadRequestResult>();
    }
}
