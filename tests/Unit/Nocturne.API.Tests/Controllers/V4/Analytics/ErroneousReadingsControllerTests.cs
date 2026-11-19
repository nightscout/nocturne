using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Controllers.V4.Analytics;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Models.Analytics;

namespace Nocturne.API.Tests.Controllers.V4.Analytics;

public class ErroneousReadingsControllerTests
{
    private readonly Mock<IErroneousReadingsService> _service = new();
    private readonly ErroneousReadingsController _controller;

    private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = new(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);

    public ErroneousReadingsControllerTests()
    {
        _controller = new ErroneousReadingsController(_service.Object);
    }

    [Fact]
    public async Task AValidRange_ReturnsTheServicesReport()
    {
        var report = new ErroneousReadingsReport();
        _service.Setup(s => s.GetAsync(Start, End, It.IsAny<CancellationToken>())).ReturnsAsync(report);

        var result = await _controller.GetErroneousReadings(Start, End);

        result.Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeSameAs(report);
    }

    [Theory]
    [MemberData(nameof(BadRanges))]
    public async Task ABadRange_ReturnsBadRequestWithoutReading(DateTime from, DateTime to, string detail)
    {
        var result = await _controller.GetErroneousReadings(from, to);

        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(400);
        problem.Value.Should().BeOfType<ProblemDetails>().Which.Detail.Should().Be(detail);
        _service.Verify(s => s.GetAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    public static TheoryData<DateTime, DateTime, string> BadRanges() => new()
    {
        { default, default, "startDate and endDate must be later than 0001-01-01." },
        { End, Start, "endDate must be after startDate." },
        {
            Start,
            Start.AddDays(V4ReadLimits.MaxDateSpanDays).Add(V4ReadLimits.DaylightSavingMargin).AddMilliseconds(1),
            $"Date range must not exceed {V4ReadLimits.MaxDateSpanDays} days."
        },
    };
}
