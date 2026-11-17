using Nocturne.Core.Clinical.Devices;
using Nocturne.Core.Clinical.LoopAnalysis;
using Nocturne.Core.Models.Timezones;

namespace Nocturne.Core.Clinical.Tests.Devices;

public class DeviceDateClockTests
{
    private static readonly TimeZoneInfo Sydney = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");

    private static long Mills(int year, int month, int day, int hour) =>
        new DateTimeOffset(year, month, day, hour, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private static TimezoneTimeline Timeline(params TimezoneTimelineEntry[] entries) => new(entries);

    [Fact]
    public void Window_SpansItsLocalCalendarDatesInTheProfileZone_WhenTheTimelineIsEmpty()
    {
        var clock = new DeviceDateClock(Timeline(), Sydney, null);

        clock.Window(new DateOnly(2024, 6, 3), new DateOnly(2024, 6, 4), openEnd: 0)
            .Should().Be(new MillsInterval(Mills(2024, 6, 2, 14), Mills(2024, 6, 4, 14)));
        clock.Window(null, null, openEnd: 42).Should().Be(new MillsInterval(long.MinValue, 42));
        clock.Reason.Should().BeNull();
    }

    [Fact]
    public void TheTimelineEntryInEffectWinsOverTheProfileZone_AndTheProfileZoneCoversBeforeIt()
    {
        var clock = new DeviceDateClock(
            Timeline(new TimezoneTimelineEntry { EffectiveFrom = new DateTime(2024, 6, 10), Timezone = "Europe/London" }),
            Sydney,
            null);

        clock.Window(new DateOnly(2024, 6, 5), new DateOnly(2024, 6, 12), openEnd: 0)
            .Should().Be(new MillsInterval(Mills(2024, 6, 4, 14), Mills(2024, 6, 12, 23)), "London is +1 in June");
        clock.LocalDate(new DateTime(2024, 6, 11, 23, 30, 0, DateTimeKind.Utc)).Should().Be(new DateOnly(2024, 6, 12));
        clock.LocalDate(new DateTime(2024, 6, 1, 15, 0, 0, DateTimeKind.Utc)).Should().Be(new DateOnly(2024, 6, 2));
    }

    [Fact]
    public void WithNeitherZone_DatesAreUtcDaysAndTheReasonIsTheProfiles()
    {
        var clock = new DeviceDateClock(Timeline(), null, TimeZoneUnavailableReason.NotConfigured);

        clock.Window(new DateOnly(2024, 6, 3), new DateOnly(2024, 6, 3), openEnd: 0)
            .Should().Be(new MillsInterval(Mills(2024, 6, 3, 0), Mills(2024, 6, 4, 0)));
        clock.LocalDate(new DateTime(2024, 6, 3, 23, 30, 0, DateTimeKind.Utc)).Should().Be(new DateOnly(2024, 6, 3));
        clock.Reason.Should().Be(TimeZoneUnavailableReason.NotConfigured);
    }
}
