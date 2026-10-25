using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.Profiles.Resolvers;

namespace Nocturne.API.Tests.Controllers.V4.Base;

public class V4ReadLimitsLocalDaySpanTests
{
    private sealed class AnyController : ControllerBase;

    private const int Cap = V4ReadLimits.MaxAnalyticsSpanDays;

    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    private readonly AnyController _controller = new();

    private static DateTime Utc(int year, int month, int day, int hour = 0, int minute = 0, int second = 0, int ms = 0) =>
        new(year, month, day, hour, minute, second, ms, DateTimeKind.Utc);

    [Fact]
    public void The_widest_preset_across_a_fall_back_is_accepted()
    {
        var from = Utc(2026, 7, 31, 23);
        var to = Utc(2026, 10, 29, 23, 59, 59, 999);

        (to - from).TotalDays.Should().BeGreaterThan(Cap);
        _controller.RejectLocalDaySpan(from, to, London, Cap).Should().BeNull();
    }

    [Fact]
    public void The_widest_preset_across_a_spring_forward_is_accepted()
    {
        var from = Utc(2026, 3, 1);
        var to = Utc(2026, 5, 29, 22, 59, 59, 999);

        _controller.RejectLocalDaySpan(from, to, London, Cap).Should().BeNull();
    }

    [Fact]
    public void A_range_of_one_more_local_day_is_rejected_across_a_fall_back()
    {
        var result = _controller.RejectLocalDaySpan(Utc(2026, 7, 31, 23), Utc(2026, 10, 30, 23, 59, 59, 999), London, Cap);

        result!.StatusCode.Should().Be(400);
        result.Value.Should().BeOfType<ProblemDetails>().Which.Detail.Should().Be($"Date range must not exceed {Cap} days.");
    }

    [Fact]
    public void A_range_of_one_more_local_day_is_rejected_across_a_spring_forward()
    {
        _controller.RejectLocalDaySpan(Utc(2026, 3, 1), Utc(2026, 5, 30, 22, 59, 59, 999), London, Cap)
            .Should().NotBeNull();
    }

    [Fact]
    public void Days_are_counted_in_the_zone_rather_than_in_utc()
    {
        var sydney = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");
        var from = Utc(2026, 1, 1, 13);
        var to = Utc(2026, 4, 1, 12, 59, 59, 999);

        _controller.RejectLocalDaySpan(from, to, TimeZoneInfo.Utc, Cap).Should().NotBeNull();
        _controller.RejectLocalDaySpan(from, to, sydney, Cap).Should().BeNull();
    }

    [Fact]
    public void A_range_with_one_bound_unset_is_not_spanned()
    {
        _controller.RejectLocalDaySpan(null, Utc(2026, 10, 29), London, Cap).Should().BeNull();
        _controller.RejectLocalDaySpan(Utc(2026, 1, 1), null, London, Cap).Should().BeNull();
    }

    private static Mock<ITherapySettingsResolver> TherapyIn(string zone)
    {
        var therapy = new Mock<ITherapySettingsResolver>();
        therapy
            .Setup(t => t.GetTimezoneAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(zone);
        return therapy;
    }

    [Theory]
    [InlineData(14)]
    [InlineData(87)]
    public async Task A_range_well_under_the_cap_is_accepted_without_a_zone_lookup(int days)
    {
        var therapy = TherapyIn("Europe/London");
        var from = Utc(2026, 1, 1);

        var result = await _controller.RejectAnalyticsSpanAsync(
            from, from.AddDays(days), TenantClockDoubles.In("Europe/London", therapy), CancellationToken.None);

        result.Should().BeNull();
        therapy.Verify(t => t.GetTimezoneAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_range_well_over_the_cap_is_refused_without_a_zone_lookup()
    {
        var therapy = TherapyIn("Europe/London");
        var from = Utc(2026, 1, 1);

        var result = await _controller.RejectAnalyticsSpanAsync(
            from, from.AddDays(93), TenantClockDoubles.In("Europe/London", therapy), CancellationToken.None);

        result!.StatusCode.Should().Be(400);
        therapy.Verify(t => t.GetTimezoneAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_range_near_the_cap_is_counted_in_the_tenant_zone()
    {
        var therapy = TherapyIn("Europe/London");

        var result = await _controller.RejectAnalyticsSpanAsync(
            Utc(2026, 7, 31, 23), Utc(2026, 10, 29, 23, 59, 59, 999), TenantClockDoubles.In("Europe/London", therapy), CancellationToken.None);

        result.Should().BeNull();
        therapy.Verify(t => t.GetTimezoneAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
