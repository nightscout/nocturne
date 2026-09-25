using FluentAssertions;
using Nocturne.API.Services.Analytics;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Services.Analytics;

[Trait("Category", "Unit")]
public class HourlyAveragedStatsTests
{
    private readonly StatisticsService _service = new();

    private static readonly DateTime FirstDay = new(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void CalculateAveragedStats_BucketsOnTheTenantClockRatherThanTheReadingsOwnOffset()
    {
        var newYork = TimeZoneHelper.GetTimeZoneInfoFromId("America/New_York");
        var entries = new[]
        {
            new SensorGlucose { Mgdl = 100, Timestamp = new DateTime(2026, 1, 15, 14, 30, 0, DateTimeKind.Utc), UtcOffset = 0 },
            new SensorGlucose { Mgdl = 110, Timestamp = new DateTime(2026, 1, 15, 14, 40, 0, DateTimeKind.Utc), UtcOffset = 600 },
            new SensorGlucose { Mgdl = 120, Timestamp = new DateTime(2026, 1, 15, 14, 50, 0, DateTimeKind.Utc) },
        };

        var stats = _service.CalculateAveragedStats(entries, newYork).ToList();

        stats.Single(s => s.Hour == 9).Count.Should().Be(3);
        stats.Where(s => s.Hour != 9).Should().OnlyContain(s => s.Count == 0);
    }

    [Fact]
    public void CalculateAveragedStats_FollowsDaylightSavingOnTheTenantClock()
    {
        var newYork = TimeZoneHelper.GetTimeZoneInfoFromId("America/New_York");
        var entries = new[]
        {
            new SensorGlucose { Mgdl = 100, Timestamp = new DateTime(2026, 1, 15, 14, 30, 0, DateTimeKind.Utc) },
            new SensorGlucose { Mgdl = 100, Timestamp = new DateTime(2026, 7, 15, 13, 30, 0, DateTimeKind.Utc) },
        };

        var stats = _service.CalculateAveragedStats(entries, newYork).ToList();

        stats.Single(s => s.Hour == 9).Count.Should().Be(2);
    }

    [Fact]
    public void CalculateAveragedStats_CountsTheLocalDaysAnHourHasReadingsOn()
    {
        var sydney = TimeZoneHelper.GetTimeZoneInfoFromId("Australia/Sydney");
        // 23:10 and 23:20 UTC on 2 March are 10:10 and 10:20 on 3 March in Sydney (UTC+11); the
        // third reading is 10:10 on 4 March. Two local days, though the UTC dates are 2 and 3 March.
        var entries = new[]
        {
            new SensorGlucose { Mgdl = 100, Timestamp = new DateTime(2026, 3, 2, 23, 10, 0, DateTimeKind.Utc) },
            new SensorGlucose { Mgdl = 100, Timestamp = new DateTime(2026, 3, 2, 23, 20, 0, DateTimeKind.Utc) },
            new SensorGlucose { Mgdl = 100, Timestamp = new DateTime(2026, 3, 3, 23, 10, 0, DateTimeKind.Utc) },
        };

        var hour = _service.CalculateAveragedStats(entries, sydney).Single(s => s.Hour == 10);

        hour.Count.Should().Be(3);
        hour.DayCount.Should().Be(2);
    }

    [Fact]
    public void CalculateAveragedStats_LeavesImplausibleReadingsOutOfTheCounts()
    {
        var entries = new[]
        {
            new SensorGlucose { Mgdl = 100, Timestamp = FirstDay.AddHours(4) },
            new SensorGlucose { Mgdl = 0, Timestamp = FirstDay.AddDays(1).AddHours(4) },
            new SensorGlucose { Mgdl = 700, Timestamp = FirstDay.AddDays(2).AddHours(4) },
        };

        var hour = _service.CalculateAveragedStats(entries, TimeZoneInfo.Utc).Single(s => s.Hour == 4);

        hour.Count.Should().Be(1);
        hour.DayCount.Should().Be(1);
    }
}
