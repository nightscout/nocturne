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

    public static TheoryData<string, int, int, int> DaylightSavingRanges => new()
    {
        { "Australia/Sydney", 2025, 4, 5 },
        { "Australia/Sydney", 2025, 10, 4 },
        { "America/New_York", 2025, 11, 1 },
        { "America/New_York", 2025, 3, 8 },
        { "Europe/London", 2025, 10, 25 },
        { "Europe/London", 2025, 3, 29 },
    };

    private static (DateTime From, DateTime To) LocalDays(string zoneId, DateTime firstDay, int days)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        var from = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(firstDay, DateTimeKind.Unspecified), zone);
        var to = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(firstDay.AddDays(days), DateTimeKind.Unspecified), zone).AddMilliseconds(-1);
        return (from, to);
    }

    [Theory]
    [MemberData(nameof(DaylightSavingRanges))]
    public void RejectDateSpan_accepts_the_cap_in_local_days_across_a_daylight_saving_change(
        string zoneId, int year, int month, int day)
    {
        var (from, to) = LocalDays(zoneId, new DateTime(year, month, day), V4ReadLimits.MaxDateSpanDays);

        _controller.RejectDateSpan(from, to).Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(DaylightSavingRanges))]
    public void RejectDateSpan_rejects_one_local_day_over_the_cap_across_a_daylight_saving_change(
        string zoneId, int year, int month, int day)
    {
        var (from, to) = LocalDays(zoneId, new DateTime(year, month, day), V4ReadLimits.MaxDateSpanDays + 1);

        _controller.RejectDateSpan(from, to)!.StatusCode.Should().Be(400);
    }

    [Fact]
    public void RejectDateSpan_accepts_exactly_the_cap_and_rejects_a_day_over_in_utc()
    {
        var start = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        _controller.RejectDateSpan(start, start.AddDays(V4ReadLimits.MaxDateSpanDays)).Should().BeNull();
        _controller.RejectDateSpan(start, start.AddDays(V4ReadLimits.MaxDateSpanDays + 1))!.StatusCode.Should().Be(400);
    }
}
