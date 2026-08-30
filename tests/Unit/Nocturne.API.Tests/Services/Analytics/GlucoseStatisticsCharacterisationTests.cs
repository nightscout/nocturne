using FluentAssertions;
using Nocturne.API.Services.Analytics;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Services.Analytics;

/// <summary>
/// Pins the numeric output of every statistic that is computed from a shared variance, median,
/// A1C or glucose-zone rule, at full double precision rather than at the one decimal place the
/// responses round to. The expected values were derived from the published formulae independently
/// of the implementation, so a divisor, a bound, or an inclusive edge that moves is reported here
/// as a wrong number rather than as a rounding coincidence.
/// </summary>
public class GlucoseStatisticsCharacterisationTests
{
    private readonly StatisticsService _service = new();

    /// <summary>
    /// A day's worth of readings that rises through the target band into hyperglycaemia and falls
    /// back through hypoglycaemia, so mean, both variance divisors, MAGE's turning points and the
    /// log-transformed metrics all have something to bite on.
    /// </summary>
    private static readonly double[] DayCurve =
    [
        95, 102, 118, 143, 167, 189, 201, 187, 165, 142, 128, 115,
        104, 98, 88, 76, 68, 61, 55, 52, 63, 84, 110, 134,
    ];

    /// <summary>
    /// One reading on each side of, and one exactly on, every default zone bound: 54, 70, 140
    /// (tight-target top), 180 and 250.
    /// </summary>
    private static readonly double[] ZoneEdges =
    [
        53, 54, 69, 70, 139, 140, 141, 179, 180, 181, 250, 251,
    ];

    /// <summary>
    /// One reading on each side of, and one exactly on, every bound of the extended hourly zone
    /// set: 54, 63, 140, 180 and 200.
    /// </summary>
    private static readonly double[] ExtendedZoneEdges =
    [
        53, 54, 62, 63, 139, 140, 179, 180, 199, 200, 201,
    ];

    #region Sample-divisor standard deviation

    [Fact]
    public void CalculateBasicStats_PinsSampleStandardDeviationAndMedian()
    {
        var result = _service.CalculateBasicStats(DayCurve);

        result.Count.Should().Be(24);
        result.Mean.Should().Be(114.4);
        result.Median.Should().Be(107.0);
        result.Min.Should().Be(52);
        result.Max.Should().Be(201);

        // sqrt(sum((v - 114.4)^2) / 23) = 44.19110671892568, rounded to one decimal.
        // CalculateMean rounds before the deviations are taken, so the centre is 114.4, not 114.375.
        result.StandardDeviation.Should().Be(44.2);
    }

    [Theory]
    [InlineData(new double[] { }, 0d, 0d)]
    [InlineData(new[] { 123d }, 123d, 123d)]
    [InlineData(new[] { 100d, 100d, 100d, 100d }, 100d, 100d)]
    public void CalculateBasicStats_PinsZeroStandardDeviationForDegenerateSeries(
        double[] values,
        double expectedMean,
        double expectedMedian)
    {
        var result = _service.CalculateBasicStats(values);

        result.Mean.Should().Be(expectedMean);
        result.Median.Should().Be(expectedMedian);
        result.StandardDeviation.Should().Be(0);
    }

    [Fact]
    public void CalculateBasicStats_PinsMedianOfAnOddLengthSeries()
    {
        _service.CalculateBasicStats([70d, 90d, 250d]).Median.Should().Be(90);
    }

    [Fact]
    public void CalculateGlycemicVariability_PinsSampleStandardDeviationAndDerivedMetrics()
    {
        var result = _service.CalculateGlycemicVariability(DayCurve, EntriesEveryFiveMinutes(DayCurve));

        // Unlike CalculateBasicStats this centres on the unrounded mean 114.375, giving
        // sqrt(sum((v - 114.375)^2) / 23) = 44.19109933990741.
        result.StandardDeviation.Should().Be(44.2);
        result.CoefficientOfVariation.Should().Be(38.6);

        // Population divisor: the two excursions exceeding sqrt(sum(dev^2) / 24) = 43.260656 are
        // 106 and 149.
        result.MeanAmplitudeGlycemicExcursions.Should().Be(127.5);
        result.AverageDailyRiskRange.Should().Be(39.2);
        result.EstimatedA1c.Should().BeApproximately(5.612369337979094, 1e-12);
    }

    [Fact]
    public void CalculateGlycemicVariability_ThrowsBelowTwoReadings()
    {
        var act = () => _service.CalculateGlycemicVariability([100d], EntriesEveryFiveMinutes([100d]));

        act.Should().Throw<ArgumentException>();
    }

    #endregion

    #region Population-divisor standard deviation

    [Fact]
    public void CalculateMage_PinsThePopulationStandardDeviationThreshold()
    {
        _service.CalculateMAGE(DayCurve).Should().BeApproximately(127.5, 1e-12);
    }

    [Fact]
    public void CalculateMage_PinsZeroWhenNoExcursionExceedsTheStandardDeviation()
    {
        _service.CalculateMAGE([100d, 100d, 100d, 100d]).Should().Be(0);
    }

    [Theory]
    [InlineData(new[] { 100d, 120d })]
    [InlineData(new double[] { })]
    public void CalculateMage_PinsZeroBelowThreeReadings(double[] values)
    {
        _service.CalculateMAGE(values).Should().Be(0);
    }

    [Fact]
    public void CalculateAdrr_PinsThePopulationStandardDeviationOfLogGlucose()
    {
        _service.CalculateADRR(DayCurve).Should().BeApproximately(39.17853755994654, 1e-12);
    }

    [Fact]
    public void CalculateAdrr_PinsZeroForIdenticalReadings()
    {
        _service.CalculateADRR([100d, 100d, 100d]).Should().BeApproximately(0, 1e-12);
    }

    [Fact]
    public void CalculateJIndex_PinsThePopulationVarianceComponent()
    {
        var mean = DayCurve.Average();

        // 0.324 * (114.375 - 112)^2 + 0.0018 * (sum(dev^2) / 24)
        _service.CalculateJIndex(DayCurve, mean).Should().BeApproximately(5.1962343749999995, 1e-12);
    }

    [Fact]
    public void CalculateSiteChangeImpact_PinsThePopulationStandardDeviationPerBucket()
    {
        var siteChange = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var deviceEvents = new[]
        {
            new DeviceEvent { Timestamp = siteChange, EventType = DeviceEventType.SiteChange },
            new DeviceEvent { Timestamp = siteChange.AddDays(10), EventType = DeviceEventType.SiteChange },
        };

        // Only these three land inside a site-change window; the rest exist solely to clear the
        // hundred-reading sufficiency floor.
        var inBucket = new[] { 100d, 130d, 190d };
        var entries = inBucket
            .Select((value, index) => new SensorGlucose
            {
                Mgdl = value,
                Timestamp = siteChange.AddMinutes(index + 1),
            })
            .Concat(Enumerable.Range(0, 100).Select(index => new SensorGlucose
            {
                Mgdl = 120,
                Timestamp = siteChange.AddDays(-90).AddMinutes(index * 5),
            }))
            .ToList();

        var result = _service.CalculateSiteChangeImpact(entries, deviceEvents);

        var bucket = result.DataPoints.Should().ContainSingle().Subject;
        bucket.MinutesFromChange.Should().Be(0);
        bucket.Count.Should().Be(3);
        bucket.AverageGlucose.Should().Be(140);

        // sqrt(sum((v - 140)^2) / 3) = 37.416573867739416, rounded to one decimal.
        bucket.StdDev.Should().Be(37.4);
    }

    #endregion

    #region Estimated A1C

    [Theory]
    [InlineData(0d, 0d)]
    [InlineData(100d, 5.111498257839721)]
    [InlineData(114.375d, 5.612369337979094)]
    [InlineData(154d, 6.99303135888502)]
    public void CalculateEstimatedA1C_PinsTheFormula(double meanGlucose, double expected)
    {
        _service.CalculateEstimatedA1C(meanGlucose).Should().BeApproximately(expected, 1e-12);
    }

    /// <summary>
    /// The string form takes the mean from <c>CalculateMean</c>, which rounds to one decimal first,
    /// so 114.375 is centred at 114.4 and the two entry points do not share an input.
    /// </summary>
    [Theory]
    [InlineData(new double[] { }, "0.0")]
    [InlineData(new[] { 100d }, "5.1")]
    [InlineData(new[] { 154d }, "7.0")]
    public void CalculateEstimatedHbA1C_PinsTheFormattedFormula(double[] values, string expected)
    {
        _service.CalculateEstimatedHbA1C(values).Should().Be(expected);
    }

    [Fact]
    public void CalculateEstimatedHbA1C_PinsTheRoundedMeanItCentresOn()
    {
        // CalculateMean gives 114.4, so the result is (114.4 + 46.7) / 28.7 = 5.6132..., not the
        // 5.6124... that the unrounded 114.375 would give. Both format as "5.6"; the pin is that
        // the string comes from the same formula as the double.
        _service.CalculateEstimatedHbA1C(DayCurve).Should().Be("5.6");
        _service.CalculateEstimatedA1C(114.4).Should().BeApproximately(5.613240418118468, 1e-12);
    }

    #endregion

    #region Five-zone bucketing

    [Fact]
    public void CalculateTimeInRange_PinsEveryDefaultZoneBoundAndItsInclusivity()
    {
        var result = _service.CalculateTimeInRange(EntriesEveryFiveMinutes(ZoneEdges));

        // 53 | 54, 69 | 70, 139, 140, 141, 179, 180 | 181, 250 | 251
        result.Percentages.VeryLow.Should().BeApproximately(8.333333333333332, 1e-12);
        result.Percentages.Low.Should().BeApproximately(16.666666666666664, 1e-12);
        result.Percentages.Target.Should().Be(50);
        result.Percentages.High.Should().BeApproximately(16.666666666666664, 1e-12);
        result.Percentages.VeryHigh.Should().BeApproximately(8.333333333333332, 1e-12);

        // The tight band is [70, 140] and overlaps the target band rather than partitioning it.
        result.Percentages.TightTarget.Should().Be(25);

        result.Durations.VeryLow.Should().Be(5);
        result.Durations.Low.Should().Be(10);
        result.Durations.Target.Should().Be(30);
        result.Durations.TightTarget.Should().Be(15);
        result.Durations.High.Should().Be(10);
        result.Durations.VeryHigh.Should().Be(5);

        result.Episodes.VeryLow.Should().Be(1);
        result.Episodes.Low.Should().Be(1);
        result.Episodes.High.Should().Be(1);
        result.Episodes.VeryHigh.Should().Be(1);
    }

    /// <summary>
    /// The per-range statistics partition on different bounds from the percentages above: the low
    /// bucket is everything under <c>Low</c>, so it swallows the very-low readings, and the high
    /// bucket is everything over <c>TargetTop</c>, so it swallows the very-high ones.
    /// </summary>
    [Fact]
    public void CalculateTimeInRange_PinsPerRangeStatisticsOnTheSampleDivisor()
    {
        var result = _service.CalculateTimeInRange(EntriesEveryFiveMinutes(ZoneEdges));

        result.RangeStats.Low.PeriodName.Should().Be("Low");
        result.RangeStats.Low.ReadingCount.Should().Be(3);
        result.RangeStats.Low.Mean.Should().Be(58.7);
        result.RangeStats.Low.Median.Should().Be(54);
        result.RangeStats.Low.StandardDeviation.Should().Be(9.0);
        result.RangeStats.Low.TimeInRange.Should().Be(25);
        result.RangeStats.Low.Min.Should().Be(53);
        result.RangeStats.Low.Max.Should().Be(69);

        result.RangeStats.Target.PeriodName.Should().Be("In Range");
        result.RangeStats.Target.ReadingCount.Should().Be(6);
        result.RangeStats.Target.Mean.Should().Be(141.5);
        result.RangeStats.Target.Median.Should().Be(140.5);
        result.RangeStats.Target.StandardDeviation.Should().Be(40.0);
        result.RangeStats.Target.TimeInRange.Should().Be(50);

        result.RangeStats.High.PeriodName.Should().Be("High");
        result.RangeStats.High.ReadingCount.Should().Be(3);
        result.RangeStats.High.Mean.Should().Be(227.3);
        result.RangeStats.High.Median.Should().Be(250);
        result.RangeStats.High.StandardDeviation.Should().Be(40.1);
        result.RangeStats.High.TimeInRange.Should().Be(25);
    }

    /// <summary>
    /// Custom thresholds where <c>Low</c> sits above <c>TargetBottom</c>: the exclusive chain and
    /// the target band are counted independently, so readings between the two are counted twice
    /// and the percentages sum past 100.
    /// </summary>
    [Fact]
    public void CalculateTimeInRange_PinsTheOverlapBetweenTheLowChainAndTheTargetBand()
    {
        var thresholds = new GlycemicThresholds
        {
            VeryLow = 50,
            Low = 90,
            TargetBottom = 70,
            TargetTop = 160,
            TightTargetBottom = 70,
            TightTargetTop = 140,
            High = 160,
            VeryHigh = 240,
        };

        var result = _service.CalculateTimeInRange(
            EntriesEveryFiveMinutes([49, 75, 120, 200, 300]), thresholds);

        result.Percentages.VeryLow.Should().Be(20);
        result.Percentages.Low.Should().Be(20);
        result.Percentages.Target.Should().Be(40);
        result.Percentages.High.Should().Be(20);
        result.Percentages.VeryHigh.Should().Be(20);
    }

    /// <summary>
    /// Custom thresholds where <c>VeryHigh</c> sits below <c>TargetTop</c>. The chain reaches
    /// <c>&gt; VeryHigh</c> before <c>&gt; TargetTop</c>, so both 120 — which no other bound
    /// admits — and 200 — which <c>&gt; TargetTop</c> would take first, were the two tested the
    /// other way round — are very high, in the percentages, the durations and the episode count
    /// alike.
    /// </summary>
    [Fact]
    public void CalculateTimeInRange_PinsVeryHighAheadOfHighWhenTheThresholdsAreInverted()
    {
        var thresholds = new GlycemicThresholds { TargetTop = 180, High = 180, VeryHigh = 100 };

        var result = _service.CalculateTimeInRange(EntriesEveryFiveMinutes([120, 200]), thresholds);

        result.Percentages.VeryHigh.Should().Be(100);
        result.Percentages.High.Should().Be(0);
        result.Durations.VeryHigh.Should().Be(10);
        result.Durations.High.Should().Be(0);
        result.Episodes.VeryHigh.Should().Be(1);
        result.Episodes.High.Should().Be(0);
    }

    [Fact]
    public void CalculateTimeInRange_PinsAnEmptyResultForNoReadings()
    {
        var result = _service.CalculateTimeInRange([]);

        result.Percentages.Target.Should().Be(0);
        result.Durations.Target.Should().Be(0);
        result.Episodes.High.Should().Be(0);
        result.RangeStats.Target.ReadingCount.Should().Be(0);
    }

    #endregion

    #region Extended six-zone bucketing

    [Fact]
    public void CalculateAveragedStats_PinsEveryExtendedZoneBoundAndItsInclusivity()
    {
        var midnight = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var entries = ExtendedZoneEdges
            .Select((value, index) => new SensorGlucose
            {
                Mgdl = value,
                Timestamp = midnight.AddMinutes(index * 5),
            })
            .ToList();

        var hour = _service.CalculateAveragedStats(entries).Single(stats => stats.Hour == 0);

        // 53 | 54, 62 | 63, 139 | 140, 179 | 180, 199 | 200, 201
        hour.Count.Should().Be(11);
        hour.TimeInRange.VeryLow.Should().Be(9.1);
        hour.TimeInRange.Low.Should().Be(18.2);
        hour.TimeInRange.Normal.Should().Be(18.2);
        hour.TimeInRange.AboveTarget.Should().Be(18.2);
        hour.TimeInRange.High.Should().Be(18.2);
        hour.TimeInRange.VeryHigh.Should().Be(18.2);
    }

    [Fact]
    public void CalculateAveragedStats_PinsZeroedExtendedZonesForAnEmptyHour()
    {
        var hour = _service.CalculateAveragedStats([]).Single(stats => stats.Hour == 7);

        hour.Count.Should().Be(0);
        hour.TimeInRange.VeryLow.Should().Be(0);
        hour.TimeInRange.VeryHigh.Should().Be(0);
    }

    #endregion

    private static List<SensorGlucose> EntriesEveryFiveMinutes(IEnumerable<double> values)
    {
        var start = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        return values
            .Select((value, index) => new SensorGlucose
            {
                Mgdl = value,
                Timestamp = start.AddMinutes(index * 5),
            })
            .ToList();
    }
}
