using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Controllers.V4.Base;

namespace Nocturne.API.Tests.Controllers.V4.Base;

public class V4ReadLimitsDateSpanTests
{
    private sealed class AnyController : ControllerBase;

    private readonly AnyController _controller = new();

    [Fact]
    public void RejectDateSpan_in_mills_rejects_bounds_whose_difference_overflows_a_long()
    {
        var result = _controller.RejectDateSpan(long.MinValue, long.MaxValue);

        result.Should().NotBeNull();
        result!.StatusCode.Should().Be(400);
    }

    [Fact]
    public void RejectDateSpan_in_mills_honours_a_named_cap()
    {
        const long start = 0;
        const long atCap = V4ReadLimits.MaxAnalyticsSpanDays * TimeSpan.MillisecondsPerDay;

        _controller.RejectDateSpan(start, atCap, V4ReadLimits.MaxAnalyticsSpanDays).Should().BeNull();
        _controller.RejectDateSpan(start, atCap + 1, V4ReadLimits.MaxAnalyticsSpanDays)!.Value
            .Should().BeOfType<ProblemDetails>()
            .Which.Detail.Should().Be($"Date range must not exceed {V4ReadLimits.MaxAnalyticsSpanDays} days.");
    }
}
