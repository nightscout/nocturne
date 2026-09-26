using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Controllers.V4.Analytics;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Models.Analytics;

namespace Nocturne.API.Tests.Controllers.V4.Analytics;

/// <summary>
/// Tests the controller's translation layer: date validation and the HypoEventOptions assembled
/// from query parameters (notably the Medium minConfidence default that the frontend relies on).
/// </summary>
public class SensorIntegrityControllerTests
{
    private readonly Mock<ISensorIntegrityService> _service = new();
    private readonly SensorIntegrityController _controller;

    private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = new(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);

    public SensorIntegrityControllerTests()
    {
        _controller = new SensorIntegrityController(_service.Object);
    }

    private void SetupService(Action<HypoEventOptions?>? captureOptions = null)
    {
        _service
            .Setup(s => s.AnalyzeAsync(
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<string?>(), It.IsAny<bool>(),
                It.IsAny<HypoEventOptions?>(), It.IsAny<DetectorConfig?>(), It.IsAny<CancellationToken>()))
            .Callback<DateTime, DateTime, string?, bool, HypoEventOptions?, DetectorConfig?, CancellationToken>(
                (_, _, _, _, opts, _, _) => captureOptions?.Invoke(opts))
            .ReturnsAsync(EmptyReport());
    }

    [Fact]
    public async Task Analyze_with_valid_range_returns_ok_and_defaults_min_confidence_to_medium()
    {
        HypoEventOptions? captured = null;
        SetupService(o => captured = o);

        var result = await _controller.Analyze(Start, End);

        result.Result.Should().BeOfType<OkObjectResult>();
        captured.Should().NotBeNull();
        captured!.MinConfidence.Should().Be(ClusterConfidence.Medium);
        captured.RequireInsulin.Should().BeFalse();
        captured.HypoThresholdMgdl.Should().Be(70.0);
        captured.WindowHours.Should().Be(3.0);
    }

    [Fact]
    public async Task Analyze_with_missing_dates_returns_bad_request_without_calling_service()
    {
        var result = await _controller.Analyze(default, default);

        ProblemOf(result).Detail.Should().Be("startDate and endDate must be later than 0001-01-01.");
        VerifyNotAnalyzed();
    }

    [Fact]
    public async Task Analyze_with_end_not_after_start_returns_bad_request()
    {
        var result = await _controller.Analyze(End, Start);

        ProblemOf(result).Detail.Should().Be("endDate must be after startDate.");
        VerifyNotAnalyzed();
    }

    [Fact]
    public async Task Analyze_with_a_range_over_the_cap_returns_bad_request_without_calling_service()
    {
        var result = await _controller.Analyze(Start, Start.AddDays(V4ReadLimits.MaxDateSpanDays).AddMilliseconds(1));

        ProblemOf(result).Detail.Should().Be($"Date range must not exceed {V4ReadLimits.MaxDateSpanDays} days.");
        VerifyNotAnalyzed();
    }

    [Fact]
    public async Task Analyze_accepts_a_range_of_exactly_the_cap()
    {
        SetupService();

        var result = await _controller.Analyze(Start, Start.AddDays(V4ReadLimits.MaxDateSpanDays));

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    private static ProblemDetails ProblemOf(ActionResult<SensorIntegrityReport> result)
    {
        var objectResult = result.Result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(400);
        return objectResult.Value.Should().BeOfType<ProblemDetails>().Subject;
    }

    private void VerifyNotAnalyzed() =>
        _service.Verify(
            s => s.AnalyzeAsync(
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<string?>(), It.IsAny<bool>(),
                It.IsAny<HypoEventOptions?>(), It.IsAny<DetectorConfig?>(), It.IsAny<CancellationToken>()),
            Times.Never);

    private static SensorIntegrityReport EmptyReport() => new()
    {
        From = Start,
        To = End,
        Clusters = [],
        HypoEvents = [],
        Summary = new SensorIntegritySummary
        {
            Days = 0,
            Clusters = 0,
            MediumClusters = 0,
            HighClusters = 0,
            Events = 0,
            NocturnalEvents = 0,
        },
    };
}
