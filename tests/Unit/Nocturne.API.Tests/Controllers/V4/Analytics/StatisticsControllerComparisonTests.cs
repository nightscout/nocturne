using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Configuration;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Analytics;

public partial class StatisticsControllerTests
{
    private static readonly DateTime PeriodAStart = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime PeriodBStart = new(2026, 6, 8, 0, 0, 0, DateTimeKind.Utc);

    private void SetupGlucoseByPeriod(double mgdlA, double? mgdlB)
    {
        List<SensorGlucose> Readings(DateTime start, double mgdl) =>
            Enumerable.Range(0, 12)
                .Select(i => new SensorGlucose { Timestamp = start.AddMinutes(5 * i), Mgdl = mgdl })
                .ToList();

        _glucoseRepoMock
            .Setup(r => r.GetForAnalyticsAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<DateTime?>(), It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime? from, DateTime? _, string? _, string? _, int _, int _, bool _, DateTime? _, Guid? _, CancellationToken _, Guid? _) =>
                from == PeriodAStart
                    ? Readings(PeriodAStart, mgdlA)
                    : mgdlB is { } b ? Readings(PeriodBStart, b) : []);
        SetupEmptyTreatments();
    }

    private async Task<PeriodComparison> Compare(GlucoseUnit? units = null)
    {
        var result = await CreateController(statisticsService: new StatisticsService(TimeProvider.System))
            .GetPeriodComparison(
                PeriodAStart, PeriodAStart.AddDays(7),
                PeriodBStart, PeriodBStart.AddDays(7),
                units);
        return result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<PeriodComparison>().Subject;
    }

    private static MetricComparison Metric(PeriodComparison comparison, PeriodComparisonMetric metric) =>
        comparison.Metrics.Single(m => m.Metric == metric);

    [Fact]
    public async Task GetPeriodComparison_ServesEachPeriodsMetricsAndTheChangeBetweenThem()
    {
        SetupGlucoseByPeriod(100, 154);

        var comparison = await Compare(GlucoseUnit.MgDl);

        comparison.PeriodA.HasData.Should().BeTrue();
        comparison.PeriodB.HasData.Should().BeTrue();
        var mean = Metric(comparison, PeriodComparisonMetric.Mean);
        mean.ValueA.Should().Be(100);
        mean.ValueB.Should().Be(154);
        mean.Delta.Should().Be(54);
        mean.PercentChange.Should().BeApproximately(54, 1e-9);
    }

    [Fact]
    public async Task GetPeriodComparison_ServesGlucoseInTheRequestedUnitsAtThatUnitsPrecision()
    {
        SetupGlucoseByPeriod(100, 154);

        var comparison = await Compare(GlucoseUnit.Mmol);

        comparison.Units.Should().Be(GlucoseUnit.Mmol);
        var mean = Metric(comparison, PeriodComparisonMetric.Mean);
        mean.ValueA.Should().Be(5.5);
        mean.ValueB.Should().Be(8.5);
        mean.Delta.Should().Be(3.0);
        mean.ValueDecimals.Should().Be(1);
        mean.DeltaDecimals.Should().Be(1);
        mean.PercentChange.Should().BeApproximately(54, 1e-9);
    }

    [Theory]
    [InlineData("mmol", GlucoseUnit.Mmol)]
    [InlineData("mg/dl", GlucoseUnit.MgDl)]
    [InlineData(null, GlucoseUnit.MgDl)]
    public async Task GetPeriodComparison_WithoutRequestedUnits_ServesTheTenantsUnits(string? tenantUnits, GlucoseUnit expected)
    {
        SetupGlucoseByPeriod(100, 154);
        _glucoseUnitsMock
            .Setup(u => u.UsesMmolAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(tenantUnits == "mmol");

        var comparison = await Compare();

        comparison.Units.Should().Be(expected);
    }

    [Fact]
    public async Task GetPeriodComparison_RequestedUnitsWinOverTheTenants()
    {
        SetupGlucoseByPeriod(100, 154);
        _glucoseUnitsMock
            .Setup(u => u.UsesMmolAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var comparison = await Compare(GlucoseUnit.MgDl);

        comparison.Units.Should().Be(GlucoseUnit.MgDl);
        Metric(comparison, PeriodComparisonMetric.Mean).ValueB.Should().Be(154);
    }

    [Fact]
    public async Task GetPeriodComparison_APeriodWithNoReadings_HasNoDataAndEveryChangeIsNull()
    {
        SetupGlucoseByPeriod(100, null);

        var comparison = await Compare(GlucoseUnit.MgDl);

        comparison.PeriodA.HasData.Should().BeTrue();
        comparison.PeriodB.HasData.Should().BeFalse();
        comparison.PeriodB.TimeInRange.Should().BeNull();
        comparison.Metrics.Should().OnlyContain(m =>
            m.ValueB == null && m.Delta == null && m.PercentChange == null);
        Metric(comparison, PeriodComparisonMetric.Mean).ValueA.Should().Be(100);
    }

    [Fact]
    public async Task GetPeriodComparison_AZeroBaseline_HasNoPercentChange()
    {
        SetupGlucoseByPeriod(100, 154);

        var comparison = await Compare(GlucoseUnit.MgDl);

        var hypo = Metric(comparison, PeriodComparisonMetric.VeryLowPercent);
        hypo.ValueA.Should().Be(0);
        hypo.ValueB.Should().Be(0);
        hypo.Delta.Should().Be(0);
        hypo.PercentChange.Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetPeriodComparison_ReadsEachPeriodsCarbsAndManualBolusesOnce(bool firstPeriod)
    {
        SetupGlucoseByPeriod(100, 154);
        var start = firstPeriod ? PeriodAStart : PeriodBStart;
        var end = start.AddDays(7);

        await Compare(GlucoseUnit.MgDl);

        _carbIntakeRepoMock.Verify(r => r.GetAsync(
            start, end, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
            It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Once);
        _bolusRepoMock.Verify(r => r.GetAsync(
            start, end, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
            BolusKind.Manual, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetPeriodComparison_RefusesAPeriodOverTheDateSpanCap(bool firstPeriod)
    {
        SetupGlucoseByPeriod(100, 154);
        var tooLong = PeriodAStart.AddDays(V4ReadLimits.MaxDateSpanDays + 1);

        var result = await CreateController(statisticsService: new StatisticsService(TimeProvider.System))
            .GetPeriodComparison(
                PeriodAStart, firstPeriod ? tooLong : PeriodAStart.AddDays(7),
                PeriodBStart, firstPeriod ? PeriodBStart.AddDays(7) : PeriodBStart.AddDays(V4ReadLimits.MaxDateSpanDays + 1),
                GlucoseUnit.MgDl);

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
    }
}
