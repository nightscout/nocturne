using FluentAssertions;
using Moq;
using Nocturne.API.Services.Alerts;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

[Trait("Category", "Unit")]
public class ReadingTrendTests
{
    private static readonly DateTime At = new(2026, 11, 17, 8, 0, 0, DateTimeKind.Utc);

    private static SensorGlucose Reading(
        DateTime at, double mgdl, double? trendRate = null, GlucoseDirection? direction = null,
        string? dataSource = "dexcom", string device = "g7", Guid? patientDeviceId = null) => new()
    {
        Id = Guid.NewGuid(),
        Timestamp = at,
        Mgdl = mgdl,
        TrendRate = trendRate,
        Direction = direction,
        DataSource = dataSource,
        Device = device,
        PatientDeviceId = patientDeviceId,
    };

    private static ReadingTrend Of(SensorGlucose reading, IEnumerable<SensorGlucose> earlier) =>
        ReadingTrend.Of(reading, earlier.Select(e => (e, (Guid?)null)));

    [Fact]
    public void A_stored_rate_is_used_unchanged()
    {
        var trend = Of(
            Reading(At, 100, trendRate: 0.4, direction: GlucoseDirection.DoubleDown),
            [Reading(At.AddMinutes(-5), 150)]);

        trend.Rate.Should().Be(0.4m);
        trend.Bucket.Should().Be(TrendBucket.Flat);
    }

    [Fact]
    public void Without_a_stored_rate_it_is_derived_from_a_five_minute_pair()
    {
        var trend = Of(Reading(At, 100), [Reading(At.AddMinutes(-5), 116)]);

        trend.Rate.Should().Be(-3.2m);
        trend.Bucket.Should().Be(TrendBucket.FallingFast);
    }

    [Fact]
    public void The_nearest_earlier_reading_is_the_pair()
    {
        var trend = Of(Reading(At, 100),
        [
            Reading(At.AddMinutes(-10), 140),
            Reading(At.AddMinutes(-1), 102),
            Reading(At.AddMinutes(-5), 110),
        ]);

        trend.Rate.Should().Be(-2m);
    }

    [Fact]
    public void A_later_reading_is_not_the_pair()
    {
        var trend = Of(Reading(At, 100),
            [Reading(At.AddMinutes(1), 60), Reading(At.AddMinutes(-5), 105)]);

        trend.Rate.Should().Be(-1m);
    }

    [Fact]
    public void A_gap_of_exactly_fifteen_minutes_derives()
    {
        var trend = Of(Reading(At, 100), [Reading(At - ReadingTrend.MaxGap, 130)]);

        trend.Rate.Should().Be(-2m);
    }

    [Fact]
    public void A_gap_over_fifteen_minutes_gives_no_rate()
    {
        var trend = Of(Reading(At, 100),
            [Reading(At - ReadingTrend.MaxGap - TimeSpan.FromSeconds(1), 160)]);

        trend.Rate.Should().BeNull();
        trend.Bucket.Should().BeNull();
    }

    [Fact]
    public void A_duplicate_timestamp_gives_no_rate_even_with_an_older_reading_in_range()
    {
        var trend = Of(Reading(At, 100),
            [Reading(At, 104), Reading(At.AddMinutes(-5), 130)]);

        trend.Rate.Should().BeNull();
    }

    [Fact]
    public void The_reading_itself_is_not_its_own_pair()
    {
        var reading = Reading(At, 100);

        var trend = Of(reading, [reading, Reading(At.AddMinutes(-5), 95)]);

        trend.Rate.Should().Be(1m);
    }

    [Fact]
    public void A_reading_of_another_uploader_is_ignored()
    {
        var trend = Of(Reading(At, 100),
            [Reading(At.AddMinutes(-1), 180, dataSource: "xdrip"), Reading(At.AddMinutes(-5), 110)]);

        trend.Rate.Should().Be(-2m);
    }

    [Fact]
    public void A_reading_of_another_registered_device_is_ignored()
    {
        var mine = Guid.NewGuid();
        var trend = Of(Reading(At, 100, patientDeviceId: mine),
        [
            Reading(At.AddMinutes(-1), 180, patientDeviceId: Guid.NewGuid()),
            Reading(At.AddMinutes(-5), 110, patientDeviceId: mine),
        ]);

        trend.Rate.Should().Be(-2m);
    }

    [Fact]
    public void A_sensor_error_reading_is_not_a_pair()
    {
        var trend = Of(Reading(At, 100),
            [Reading(At.AddMinutes(-1), 0), Reading(At.AddMinutes(-5), 110)]);

        trend.Rate.Should().Be(-2m);
    }

    [Fact]
    public void A_sensor_error_reading_has_no_derived_rate()
    {
        var trend = Of(Reading(At, 0), [Reading(At.AddMinutes(-5), 110)]);

        trend.Rate.Should().BeNull();
    }

    [Fact]
    public void Without_a_rate_the_arrow_gives_the_bucket_and_the_rate_stays_null()
    {
        var trend = Of(Reading(At, 100, direction: GlucoseDirection.DoubleDown), []);

        trend.Rate.Should().BeNull();
        trend.Bucket.Should().Be(TrendBucket.FallingFast);
    }

    [Fact]
    public void A_derived_rate_wins_over_the_arrow()
    {
        var trend = Of(Reading(At, 100, direction: GlucoseDirection.DoubleDown),
            [Reading(At.AddMinutes(-5), 100)]);

        trend.Rate.Should().Be(0m);
        trend.Bucket.Should().Be(TrendBucket.Flat);
    }

    [Theory]
    [InlineData(3.5, TrendBucket.RisingFast)]
    [InlineData(3.0, TrendBucket.RisingFast)]
    [InlineData(1.0, TrendBucket.Rising)]
    [InlineData(0.0, TrendBucket.Flat)]
    [InlineData(-1.0, TrendBucket.Flat)]
    [InlineData(-1.5, TrendBucket.Falling)]
    [InlineData(-3.0, TrendBucket.Falling)]
    [InlineData(-3.5, TrendBucket.FallingFast)]
    public void Rate_bucket_boundaries(double rate, TrendBucket expected) =>
        ReadingTrend.BucketOf(rate.ToDecimal()).Should().Be(expected);

    [Theory]
    [InlineData(GlucoseDirection.DoubleUp, TrendBucket.RisingFast)]
    [InlineData(GlucoseDirection.SingleUp, TrendBucket.Rising)]
    [InlineData(GlucoseDirection.FortyFiveUp, TrendBucket.Rising)]
    [InlineData(GlucoseDirection.Flat, TrendBucket.Flat)]
    [InlineData(GlucoseDirection.FortyFiveDown, TrendBucket.Falling)]
    [InlineData(GlucoseDirection.SingleDown, TrendBucket.Falling)]
    [InlineData(GlucoseDirection.DoubleDown, TrendBucket.FallingFast)]
    [InlineData(GlucoseDirection.None, null)]
    [InlineData(GlucoseDirection.NotComputable, null)]
    [InlineData(GlucoseDirection.RateOutOfRange, null)]
    [InlineData(null, null)]
    public void Arrow_bucket(GlucoseDirection? direction, TrendBucket? expected) =>
        ReadingTrend.BucketOf(direction).Should().Be(expected);

    [Fact]
    public void A_gap_of_exactly_thirty_seconds_derives()
    {
        var trend = Of(Reading(At, 100), [Reading(At - ReadingTrend.MinGap, 101)]);

        trend.Rate.Should().Be(-2m);
    }

    [Fact]
    public void A_gap_under_thirty_seconds_gives_no_rate_even_with_an_older_reading_in_range()
    {
        var trend = Of(Reading(At, 100),
            [Reading(At - ReadingTrend.MinGap + TimeSpan.FromSeconds(1), 101), Reading(At.AddMinutes(-5), 130)]);

        trend.Rate.Should().BeNull();
    }

    [Fact]
    public void A_hidden_copy_of_the_streams_previous_reading_is_the_pair()
    {
        var reading = Reading(At, 100, dataSource: "xdrip");

        var trend = ReadingTrend.Of(reading,
        [
            (Reading(At.AddMinutes(-10), 140, dataSource: "xdrip"), null),
            (Reading(At.AddMinutes(-5), 110, dataSource: "xdrip"), Guid.NewGuid()),
        ]);

        trend.Rate.Should().Be(-2m);
    }

    /// <summary>
    /// One uploader sends the reading twice under two ids at one timestamp, and deduplication
    /// links the copies. Taken as the earlier reading, the copy would give a zero gap and no rate.
    /// </summary>
    [Fact]
    public void A_copy_in_the_readings_own_duplicate_group_is_not_the_pair()
    {
        var reading = Reading(At, 100);

        var trend = ReadingTrend.Of(reading,
        [
            (Reading(At, 100), reading.Id),
            (Reading(At.AddMinutes(-5), 110), null),
        ]);

        trend.Rate.Should().Be(-2m);
    }

    /// <summary>
    /// Two apps upload one registered CGM, their copies of each reading 40 seconds apart, too far
    /// apart for deduplication to link them. The values fall 20 mg/dL every 5 minutes; the xdrip
    /// copy reads <paramref name="offset"/> mg/dL off the dexcom one.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(4)]
    public void Another_uploaders_unlinked_copy_of_a_registered_device_is_never_the_pair(int offset)
    {
        var device = Guid.NewGuid();
        var reading = Reading(At.AddSeconds(40), 100 + offset, dataSource: "xdrip", patientDeviceId: device);

        var trend = Of(reading,
        [
            Reading(At.AddMinutes(-5), 120, dataSource: "dexcom", patientDeviceId: device),
            Reading(At.AddMinutes(-5).AddSeconds(40), 120 + offset, dataSource: "xdrip", patientDeviceId: device),
            Reading(At, 100, dataSource: "dexcom", patientDeviceId: device),
        ]);

        trend.Rate.Should().Be(-4m);
        trend.Bucket.Should().Be(TrendBucket.FallingFast);
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData(null, "", true)]
    [InlineData("", null, true)]
    [InlineData(null, "dexcom", false)]
    [InlineData("dexcom", null, false)]
    public void A_null_and_an_empty_data_source_are_one_uploader_and_match_no_other(
        string? source, string? earlierSource, bool derives)
    {
        var device = Guid.NewGuid();
        var reading = Reading(At, 100, dataSource: source, patientDeviceId: device);

        var trend = Of(reading,
            [Reading(At.AddMinutes(-5), 110, dataSource: earlierSource, patientDeviceId: device)]);

        trend.Rate.Should().Be(derives ? -2m : null);
    }

    [Fact]
    public void A_hidden_copy_of_another_stream_is_never_the_pair()
    {
        var reading = Reading(At, 100, dataSource: "xdrip");

        var trend = ReadingTrend.Of(reading,
        [
            (Reading(At.AddMinutes(-1), 180, dataSource: "dexcom"), Guid.NewGuid()),
            (Reading(At.AddMinutes(-5), 110, dataSource: "xdrip"), null),
        ]);

        trend.Rate.Should().Be(-2m);
    }

    [Fact]
    public async Task A_stored_rate_reads_nothing()
    {
        var canonical = new Mock<ICanonicalGlucoseService>(MockBehavior.Strict);

        var trend = await ReadingTrend.ForAsync(Reading(At, 100, trendRate: -2), canonical.Object, CancellationToken.None);

        trend.Rate.Should().Be(-2m);
    }

    [Fact]
    public async Task Without_a_stored_rate_one_read_covers_the_gap_before_the_reading()
    {
        var reading = Reading(At, 100);
        var canonical = new Mock<ICanonicalGlucoseService>();
        canonical.Setup(c => c.GetStreamPredecessorAsync(reading, At - ReadingTrend.MaxGap, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Reading(At.AddMinutes(-5), 120));

        var trend = await ReadingTrend.ForAsync(reading, canonical.Object, CancellationToken.None);

        trend.Rate.Should().Be(-4m);
        canonical.Verify(
            c => c.GetStreamPredecessorAsync(It.IsAny<SensorGlucose>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task A_predecessor_exactly_thirty_seconds_old_derives()
    {
        var reading = Reading(At, 100);
        var canonical = new Mock<ICanonicalGlucoseService>();
        canonical.Setup(c => c.GetStreamPredecessorAsync(reading, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Reading(At - ReadingTrend.MinGap, 101));

        var trend = await ReadingTrend.ForAsync(reading, canonical.Object, CancellationToken.None);

        trend.Rate.Should().Be(-2m);
    }

    [Fact]
    public async Task A_predecessor_under_thirty_seconds_old_gives_no_rate()
    {
        var reading = Reading(At, 100);
        var canonical = new Mock<ICanonicalGlucoseService>();
        canonical.Setup(c => c.GetStreamPredecessorAsync(reading, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Reading(At.AddSeconds(-29), 101));

        var trend = await ReadingTrend.ForAsync(reading, canonical.Object, CancellationToken.None);

        trend.Rate.Should().BeNull();
    }
}
