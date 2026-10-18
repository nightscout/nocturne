using Nocturne.API.Services.Analytics;
using Nocturne.Core.Models;

namespace Nocturne.API.Tests.Services.Analytics;

public class LocalDayGroupingTests
{
    [Theory]
    [InlineData("UTC", 2024)]
    [InlineData("America/New_York", 2024)]
    [InlineData("Australia/Sydney", 2024)]
    [InlineData("Asia/Kathmandu", 2024)]
    [InlineData("America/New_York", 1883)]
    public void Partition_matches_timezone_dates_throughout_the_year(string timezone, int year)
    {
        var zone = TimeZoneHelper.GetTimeZoneInfoFromId(timezone);
        var grouping = LocalDayGrouping.Create(year, zone);
        grouping.Should().NotBeNull();
        var classify = grouping!.Compile();
        var start = TimeZoneInfo.ConvertTimeToUtc(new DateTime(year, 1, 1), zone);
        var end = TimeZoneInfo.ConvertTimeToUtc(new DateTime(year + 1, 1, 1), zone);
        for (var timestamp = start; timestamp < end; timestamp = timestamp.AddMinutes(15))
        {
            classify(timestamp).Date.Should().Be(TimeZoneInfo.ConvertTimeFromUtc(timestamp, zone).Date);
        }
    }

    [Theory]
    [InlineData("Pacific/Apia", 2011)]
    [InlineData("America/Havana", 2024)]
    public void Midnight_transitions_use_the_existing_timestamp_grouping(string timezone, int year)
    {
        LocalDayGrouping.Create(year, TimeZoneHelper.GetTimeZoneInfoFromId(timezone))
            .Should().BeNull();
    }
}
