using FluentAssertions;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Core.Models.Tests.V4;

public class ScheduleEntryTimeTests
{
    [Theory]
    [InlineData("06:00", 21600)]
    [InlineData("6:00", 21600)]
    [InlineData("06:00:00", 21600)]
    [InlineData("23:59:59", 86399)]
    public void ASpelledTimeOfDay_IsTheSameSecondsOnBothPaths(string time, int seconds)
    {
        ScheduleEntryTime.SecondsOf(null, time).Should().Be(seconds);

        var value = new TimeValue { Time = time };
        value.EnsureTimeAsSeconds();
        value.TimeAsSeconds.Should().Be(seconds);
    }

    [Theory]
    [InlineData("6")]
    [InlineData("")]
    [InlineData("not a time")]
    public void ABareNumberOrGarbage_IsNotATimeOfDay_OnEitherPath(string time)
    {
        ScheduleEntryTime.SecondsOf(null, time).Should().Be(0);

        var value = new TimeValue { Time = time };
        value.EnsureTimeAsSeconds();
        value.TimeAsSeconds.Should().BeNull();
    }

    [Fact]
    public void ASuffixedTime_IsRejectedOnBothPaths()
    {
        ScheduleEntryTime.SecondsOf(null, "6:00 AM").Should().Be(0);

        var value = new TimeValue { Time = "6:00 AM" };
        value.EnsureTimeAsSeconds();
        value.TimeAsSeconds.Should().BeNull();
    }

    [Fact]
    public void AStoredTimeAsSeconds_WinsOverTheText()
    {
        ScheduleEntryTime.SecondsOf(3600, "06:00").Should().Be(3600);
    }

    private static ScheduleEntry At(int seconds, double value) =>
        new() { Time = "00:00", TimeAsSeconds = seconds, Value = value };

    private static ScheduleEntry? EntryAt(IEnumerable<ScheduleEntry> entries, int seconds) =>
        ScheduleEntryTime.TryEntryAt(
            entries, seconds, e => ScheduleEntryTime.SecondsOf(e.TimeAsSeconds, e.Time), out var entry)
            ? entry
            : null;

    [Fact]
    public void EmptyEntries_ReturnsNull() => EntryAt([], 3600).Should().BeNull();

    [Fact]
    public void UnorderedEntries_ReturnTheLastStartedAtOrBeforeTheTime()
    {
        var entries = new[] { At(43200, 3), At(0, 1), At(21600, 2) };

        EntryAt(entries, 30000)!.Value.Should().Be(2);
        EntryAt(entries, 21600)!.Value.Should().Be(2);
        EntryAt(entries, 86399)!.Value.Should().Be(3);
    }

    [Fact]
    public void TimeBeforeEveryEntry_ReturnsTheEarliestEntry()
    {
        var entries = new[] { At(43200, 3), At(21600, 2) };

        EntryAt(entries, 100)!.Value.Should().Be(2);
    }

    [Fact]
    public void EntriesStartingTogether_ReturnTheLastListed()
    {
        var entries = new[] { At(0, 1), At(0, 2) };

        EntryAt(entries, 10)!.Value.Should().Be(2);
    }

    [Fact]
    public void EarliestEntriesStartingTogether_ReturnTheFirstListed()
    {
        var entries = new[] { At(100, 1), At(100, 2) };

        EntryAt(entries, 50)!.Value.Should().Be(1);
    }

    [Fact]
    public void ValueTypeEntries_AreResolved()
    {
        var entries = new[] { (Seconds: 43200, Value: 3.0), (Seconds: 0, Value: 1.0) };

        ScheduleEntryTime.TryEntryAt(entries, 50000, e => e.Seconds, out var entry).Should().BeTrue();
        entry.Value.Should().Be(3.0);
        ScheduleEntryTime.TryEntryAt(Array.Empty<(int Seconds, double Value)>(), 50000, e => e.Seconds, out _)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("2026-10-03T15:30:00Z", 5400)]
    [InlineData("2026-10-03T15:59:59Z", 7199)]
    [InlineData("2026-10-03T16:00:00Z", 10800)]
    [InlineData("2027-04-03T15:30:00Z", 9000)]
    [InlineData("2027-04-03T15:59:59Z", 10799)]
    [InlineData("2027-04-03T16:00:00Z", 7200)]
    public void SecondsOfDay_FollowsTheZoneAcrossDst(string instant, int seconds)
    {
        var sydney = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");
        var mills = DateTimeOffset.Parse(instant).ToUnixTimeMilliseconds();

        ScheduleEntryTime.SecondsOfDay(mills, sydney).Should().Be(seconds);
    }

    [Fact]
    public void SecondsOfDay_ReadsANullZoneAsUtc()
    {
        var mills = DateTimeOffset.Parse("2026-10-03T15:30:00Z").ToUnixTimeMilliseconds();

        ScheduleEntryTime.SecondsOfDay(mills, null).Should().Be(15 * 3600 + 1800);
        ScheduleEntryTime.SecondsOfDay(mills, TimeZoneInfo.Utc).Should().Be(15 * 3600 + 1800);
    }
}
