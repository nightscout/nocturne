using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Controllers.V4.Analytics;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Models;

namespace Nocturne.API.Tests.Controllers.V4.Analytics;

public class ActogramControllerTests
{
    private const long Start = 1_767_225_600_000;
    private const long AtCap = Start + V4ReadLimits.MaxActogramSpanDays * TimeSpan.MillisecondsPerDay;

    private readonly Mock<IActogramReportService> _service = new();
    private readonly ActogramController _controller;

    public ActogramControllerTests()
    {
        _service
            .Setup(s => s.GetAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActogramReportData());

        _controller = new ActogramController(_service.Object, NullLogger<ActogramController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    [Fact]
    public async Task GetActogram_a_day_over_the_cap_returns_bad_request_without_reading()
    {
        var result = await _controller.GetActogram(Start, AtCap + TimeSpan.MillisecondsPerDay);

        var objectResult = result.Result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(400);
        objectResult.Value.Should().BeOfType<ProblemDetails>()
            .Which.Detail.Should().Be($"Date range must not exceed {V4ReadLimits.MaxActogramSpanDays} days.");
        _service.Verify(
            s => s.GetAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetActogram_at_the_cap_is_accepted()
    {
        var result = await _controller.GetActogram(Start, AtCap);

        result.Result.Should().BeOfType<OkObjectResult>();
    }
}
