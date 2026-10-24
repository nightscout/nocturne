using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Controllers.V4.Sleep;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.Sleep;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Sleep.Report;

namespace Nocturne.API.Tests.Controllers.V4.Sleep;

public class SleepReportControllerTests
{
    private static readonly DateTime From = new(2025, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly Mock<ISleepReportService> _service = new();
    private readonly SleepReportController _controller;

    public SleepReportControllerTests()
    {
        _service
            .Setup(s => s.GetTrendsReportAsync(
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<SleepSource?>(), It.IsAny<TimeZoneInfo?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SleepTrendsReport());

        _controller = new SleepReportController(_service.Object, TenantClockDoubles.In("Europe/London"));
    }

    private void VerifyNotBuilt() =>
        _service.Verify(
            s => s.GetTrendsReportAsync(
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<SleepSource?>(), It.IsAny<TimeZoneInfo?>(), It.IsAny<CancellationToken>()),
            Times.Never);

    [Fact]
    public async Task GetTrends_over_the_analytics_cap_returns_bad_request_without_building()
    {
        var result = await _controller.GetTrends(From, From.AddDays(V4ReadLimits.MaxAnalyticsSpanDays));

        var objectResult = result.Result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(400);
        objectResult.Value.Should().BeOfType<ProblemDetails>()
            .Which.Detail.Should().Be($"Date range must not exceed {V4ReadLimits.MaxAnalyticsSpanDays} days.");
        VerifyNotBuilt();
    }

    [Fact]
    public async Task GetTrends_at_the_analytics_cap_is_accepted()
    {
        var result = await _controller.GetTrends(From, From.AddDays(V4ReadLimits.MaxAnalyticsSpanDays).AddMilliseconds(-1));

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetTrends_accepts_the_widest_preset_across_a_fall_back()
    {
        var result = await _controller.GetTrends(
            new DateTime(2026, 7, 31, 23, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 29, 23, 59, 59, 999, DateTimeKind.Utc));

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetTrends_accepts_the_widest_preset_across_a_spring_forward()
    {
        var result = await _controller.GetTrends(
            new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 5, 29, 22, 59, 59, 999, DateTimeKind.Utc));

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetTrends_refuses_91_local_days_across_a_fall_back()
    {
        var result = await _controller.GetTrends(
            new DateTime(2026, 7, 31, 23, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 30, 23, 59, 59, 999, DateTimeKind.Utc));

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
        VerifyNotBuilt();
    }

    [Fact]
    public async Task GetTrends_builds_the_report_in_the_tenants_zone()
    {
        await _controller.GetTrends(From, From.AddDays(3));

        _service.Verify(
            s => s.GetTrendsReportAsync(
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<SleepSource?>(),
                It.Is<TimeZoneInfo?>(z => z != null && z.Id == "Europe/London"), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetTrends_without_a_zone_builds_the_report_on_no_zone_and_says_why()
    {
        var controller = new SleepReportController(_service.Object, TenantClockDoubles.In(null));

        var result = await controller.GetTrends(From, From.AddDays(3));

        var report = result.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<SleepTrendsReport>().Subject;
        report.TimeZoneUnavailableReason.Should().Be(TimeZoneUnavailableReason.NotConfigured);
        _service.Verify(
            s => s.GetTrendsReportAsync(
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<SleepSource?>(), null, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetTrends_on_a_tenant_zone_carries_no_reason()
    {
        var result = await _controller.GetTrends(From, From.AddDays(3));

        result.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<SleepTrendsReport>().Subject
            .TimeZoneUnavailableReason.Should().BeNull();
    }
}
