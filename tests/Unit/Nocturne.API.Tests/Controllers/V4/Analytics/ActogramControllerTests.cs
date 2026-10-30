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

    private const long End = Start + 2 * TimeSpan.MillisecondsPerDay;

    private readonly Mock<IActogramReportService> _service = new();
    private readonly ActogramController _controller;

    public ActogramControllerTests()
    {
        _service
            .Setup(s => s.GetAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<long?>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
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
            s => s.GetAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<long?>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetActogram_a_summary_range_starting_before_the_window_returns_bad_request_without_reading()
    {
        var result = await _controller.GetActogram(Start, End, summaryStartTime: Start - 1, summaryEndTime: End);

        AssertBadRequestWithoutReading(result);
    }

    [Fact]
    public async Task GetActogram_a_summary_range_ending_after_the_window_returns_bad_request_without_reading()
    {
        var result = await _controller.GetActogram(Start, End, summaryStartTime: Start, summaryEndTime: End + 1);

        AssertBadRequestWithoutReading(result);
    }

    [Fact]
    public async Task GetActogram_an_empty_summary_range_returns_bad_request_without_reading()
    {
        var result = await _controller.GetActogram(Start, End, summaryStartTime: Start + 1000, summaryEndTime: Start + 1000);

        AssertBadRequestWithoutReading(result);
    }

    [Fact]
    public async Task GetActogram_a_backwards_summary_range_returns_bad_request_without_reading()
    {
        var result = await _controller.GetActogram(Start, End, summaryStartTime: Start + 1000, summaryEndTime: Start + 999);

        AssertBadRequestWithoutReading(result);
    }

    [Fact]
    public async Task GetActogram_omitted_summary_bounds_pass_the_window_to_the_service()
    {
        await _controller.GetActogram(Start, End);

        _service.Verify(s => s.GetAsync(Start, End, Start, End, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetActogram_a_summary_range_within_the_window_is_passed_to_the_service()
    {
        var result = await _controller.GetActogram(Start, End, summaryStartTime: Start + 1000, summaryEndTime: End - 1000);

        result.Result.Should().BeOfType<OkObjectResult>();
        _service.Verify(s => s.GetAsync(Start, End, Start + 1000, End - 1000, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetActogram_one_omitted_summary_bound_defaults_to_that_end_of_the_window()
    {
        await _controller.GetActogram(Start, End, summaryStartTime: Start + 1000);

        _service.Verify(s => s.GetAsync(Start, End, Start + 1000, End, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetActogram_at_the_cap_is_accepted()
    {
        var result = await _controller.GetActogram(Start, AtCap);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    private void AssertBadRequestWithoutReading(ActionResult<ActogramReportData> result)
    {
        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
        _service.Verify(
            s => s.GetAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<long?>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
