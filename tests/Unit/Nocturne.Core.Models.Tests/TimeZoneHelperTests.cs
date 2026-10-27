using FluentAssertions;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.Core.Models.Tests;

[Trait("Category", "Unit")]
public class TimeZoneHelperTests
{
    [Fact]
    public void GetTimeZoneInfoFromId_ExactSystemId_Resolves()
    {
        var zone = FirstAlphabeticSystemZone();

        TimeZoneHelper.GetTimeZoneInfoFromId(zone.Id).Should().Be(zone);
    }

    [Fact]
    public void GetTimeZoneInfoFromId_MisCasedId_StillResolves()
    {
        // The reported prod failure: connector data carried a mis-cased IANA ID (e.g. "ETC/GMT-2"
        // for "Etc/GMT-2"), which the case-sensitive zoneinfo lookup rejected. Resolution must be
        // case-insensitive so the intended offset is preserved instead of degrading to UTC.
        // Uses whatever ID form the running platform exposes (IANA on Unix, Windows IDs on Windows)
        // so the assertion holds cross-platform.
        var zone = FirstAlphabeticSystemZone();
        var misCased = ToggleCase(zone.Id);

        TimeZoneHelper.GetTimeZoneInfoFromId(misCased).Should().Be(zone);
    }

    [Fact]
    public void GetTimeZoneInfoFromId_MisCasedEtcGmt_ResolvesToIntendedOffset()
    {
        // Exact prod value. Etc/GMT-2 is UTC+2 (POSIX inverts the sign). The IANA zoneinfo source
        // is Unix-only; on Windows the tz source is the registry, where this IANA ID does not apply.
        if (OperatingSystem.IsWindows())
            return;

        TimeZoneHelper.GetTimeZoneInfoFromId("ETC/GMT-2").BaseUtcOffset.Should().Be(TimeSpan.FromHours(2));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Not/A_Real_Zone")]
    public void GetTimeZoneInfoFromId_UnresolvableOrEmpty_FallsBackToUtc(string? id)
    {
        TimeZoneHelper.GetTimeZoneInfoFromId(id).Should().Be(TimeZoneInfo.Utc);
    }

    [Fact]
    public void TryGetTimeZoneInfoFromId_MisCasedId_ReturnsTrue()
    {
        var zone = FirstAlphabeticSystemZone();

        TimeZoneHelper.TryGetTimeZoneInfoFromId(ToggleCase(zone.Id), out var tz).Should().BeTrue();
        tz.Should().Be(zone);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Not/A_Real_Zone")]
    public void TryGetTimeZoneInfoFromId_UnresolvableOrEmpty_ReturnsFalseAndUtc(string? id)
    {
        TimeZoneHelper.TryGetTimeZoneInfoFromId(id, out var tz).Should().BeFalse();
        tz.Should().Be(TimeZoneInfo.Utc);
    }

    [Theory]
    [InlineData(22, 6, 8)]
    [InlineData(23, 7, 8)]
    [InlineData(9, 17, 8)]
    [InlineData(1, 9, 8)]
    [InlineData(0, 23, 23)]
    [InlineData(23, 0, 1)]
    [InlineData(7, 7, 24)]
    public void GetOvernightWindow_spansOneSleepSpanFromBedtime(int bedtimeHour, int wakeTimeHour, int expectedHours)
    {
        var night = new DateOnly(2026, 2, 1);

        var (start, end) = TimeZoneHelper.GetOvernightWindow(night, TimeZoneInfo.Utc, bedtimeHour, wakeTimeHour);

        TimeSpan.FromMilliseconds(end - start).Should().Be(TimeSpan.FromHours(expectedHours));
        DateTimeOffset.FromUnixTimeMilliseconds(start).Hour.Should().Be(bedtimeHour);
        DateTimeOffset.FromUnixTimeMilliseconds(end).Hour.Should().Be(wakeTimeHour);
    }

    [Theory]
    [InlineData(22, 6, 2026, 2, 2)]
    [InlineData(9, 17, 2026, 2, 1)]
    [InlineData(1, 9, 2026, 2, 1)]
    public void GetOvernightWindow_endsOnTheNextWakeAfterBedtime(
        int bedtimeHour, int wakeTimeHour, int year, int month, int endDay)
    {
        var (_, end) = TimeZoneHelper.GetOvernightWindow(
            new DateOnly(2026, 2, 1), TimeZoneInfo.Utc, bedtimeHour, wakeTimeHour);

        DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(end).UtcDateTime)
            .Should().Be(new DateOnly(year, month, endDay));
    }

    [Theory]
    [InlineData(2026, 3, 7, 7)]
    [InlineData(2026, 10, 31, 9)]
    [InlineData(2026, 11, 1, 8)]
    public void GetOvernightWindow_followsTheLocalClockAcrossADaylightSavingChange(
        int year, int month, int day, int expectedHours)
    {
        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        var (start, end) = TimeZoneHelper.GetOvernightWindow(new DateOnly(year, month, day), newYork, 23, 7);

        TimeSpan.FromMilliseconds(end - start).Should().Be(TimeSpan.FromHours(expectedHours));
    }

    [Theory]
    [InlineData(22, 6)]
    [InlineData(9, 17)]
    [InlineData(1, 9)]
    public void GetOvernightWindow_endsBeforeTheNextNightStarts(int bedtimeHour, int wakeTimeHour)
    {
        var night = new DateOnly(2026, 2, 1);

        var (_, end) = TimeZoneHelper.GetOvernightWindow(night, TimeZoneInfo.Utc, bedtimeHour, wakeTimeHour);
        var (nextStart, _) = TimeZoneHelper.GetOvernightWindow(night.AddDays(1), TimeZoneInfo.Utc, bedtimeHour, wakeTimeHour);

        end.Should().BeLessThan(nextStart);
    }

    [Fact]
    public void GetOvernightWindow_endsWhereTheNextNightStartsForAFullDaySpan()
    {
        var night = new DateOnly(2026, 2, 1);

        var (_, end) = TimeZoneHelper.GetOvernightWindow(night, TimeZoneInfo.Utc, 7, 7);
        var (nextStart, _) = TimeZoneHelper.GetOvernightWindow(night.AddDays(1), TimeZoneInfo.Utc, 7, 7);

        end.Should().Be(nextStart);
    }

    [Fact]
    public void GetOvernightWindow_movesABedtimeInALondonSpringForwardGapToTheEndOfTheGap()
    {
        var london = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

        var (start, end) = TimeZoneHelper.GetOvernightWindow(new DateOnly(2026, 3, 29), london, 1, 9);

        DateTimeOffset.FromUnixTimeMilliseconds(start).Should().Be(new DateTimeOffset(2026, 3, 29, 1, 0, 0, TimeSpan.Zero));
        DateTimeOffset.FromUnixTimeMilliseconds(end).Should().Be(new DateTimeOffset(2026, 3, 29, 8, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void GetOvernightWindow_movesABedtimeInANewYorkSpringForwardGapToTheEndOfTheGap()
    {
        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        var (start, end) = TimeZoneHelper.GetOvernightWindow(new DateOnly(2026, 3, 8), newYork, 2, 10);

        DateTimeOffset.FromUnixTimeMilliseconds(start).Should().Be(new DateTimeOffset(2026, 3, 8, 7, 0, 0, TimeSpan.Zero));
        DateTimeOffset.FromUnixTimeMilliseconds(end).Should().Be(new DateTimeOffset(2026, 3, 8, 14, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void GetOvernightWindow_movesAWakeTimeInASpringForwardGapToTheEndOfTheGap()
    {
        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        var (_, end) = TimeZoneHelper.GetOvernightWindow(new DateOnly(2026, 3, 7), newYork, 22, 2);

        DateTimeOffset.FromUnixTimeMilliseconds(end).Should().Be(new DateTimeOffset(2026, 3, 8, 7, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void GetOvernightWindow_takesTheFirstOccurrenceOfAFallBackAmbiguousBedtime()
    {
        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        var (start, _) = TimeZoneHelper.GetOvernightWindow(new DateOnly(2026, 11, 1), newYork, 1, 9);

        DateTimeOffset.FromUnixTimeMilliseconds(start).Should().Be(new DateTimeOffset(2026, 11, 1, 5, 0, 0, TimeSpan.Zero));
    }

    [Theory]
    [InlineData(22, 6, 8)]
    [InlineData(1, 9, 8)]
    [InlineData(9, 17, 8)]
    [InlineData(7, 7, 24)]
    [InlineData(23, 0, 1)]
    public void SleepSpanHours_countsForwardFromBedtimeToWake(int bedtimeHour, int wakeTimeHour, int expected)
    {
        TimeZoneHelper.SleepSpanHours(bedtimeHour, wakeTimeHour).Should().Be(expected);
    }

    [Fact]
    public void GetOvernightWindow_cutsADaytimeWindowFromTheLocalDayAcrossADaylightSavingChange()
    {
        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        var (start, end) = TimeZoneHelper.GetOvernightWindow(new DateOnly(2026, 3, 8), newYork, 9, 17);

        TimeSpan.FromMilliseconds(end - start).Should().Be(TimeSpan.FromHours(8));
        DateTimeOffset.FromUnixTimeMilliseconds(start).Should().Be(new DateTimeOffset(2026, 3, 8, 13, 0, 0, TimeSpan.Zero));
    }

    [Theory]
    [InlineData("America/New_York", "2026-01-15T12:00:00", "2026-01-15T17:00:00")]
    [InlineData("Europe/Berlin", "2026-01-15T12:00:00", "2026-01-15T11:00:00")]
    [InlineData("Australia/Lord_Howe", "2026-01-15T12:00:00", "2026-01-15T01:00:00")]
    public void LocalToUtc_appliesTheOffsetInForceToAnOrdinaryTime(string zoneId, string local, string expectedUtc)
    {
        var result = TimeZoneHelper.LocalToUtc(DateTime.Parse(local), TimeZoneInfo.FindSystemTimeZoneById(zoneId));

        result.Should().Be(DateTime.Parse(expectedUtc));
        result.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Theory]
    [InlineData("America/New_York", "2026-03-08T02:00:00", "2026-03-08T07:00:00")]
    [InlineData("America/New_York", "2026-03-08T02:30:00", "2026-03-08T07:00:00")]
    [InlineData("America/New_York", "2026-03-08T02:59:59.9999999", "2026-03-08T07:00:00")]
    [InlineData("Europe/Berlin", "2026-03-29T02:00:00", "2026-03-29T01:00:00")]
    [InlineData("Europe/Berlin", "2026-03-29T02:30:00", "2026-03-29T01:00:00")]
    [InlineData("Europe/Berlin", "2026-03-29T02:59:59.9999999", "2026-03-29T01:00:00")]
    [InlineData("Australia/Lord_Howe", "2026-10-04T02:00:00", "2026-10-03T15:30:00")]
    [InlineData("Australia/Lord_Howe", "2026-10-04T02:15:00", "2026-10-03T15:30:00")]
    [InlineData("Australia/Lord_Howe", "2026-10-04T02:29:59.9999999", "2026-10-03T15:30:00")]
    public void LocalToUtc_mapsATimeInASpringForwardGapToTheFirstInstantAfterTheGap(
        string zoneId, string local, string expectedUtc)
    {
        var result = TimeZoneHelper.LocalToUtc(DateTime.Parse(local), TimeZoneInfo.FindSystemTimeZoneById(zoneId));

        result.Should().Be(DateTime.Parse(expectedUtc));
    }

    [Theory]
    [InlineData("America/New_York", "2026-03-08T01:59:59.9999999", "2026-03-08T06:59:59.9999999")]
    [InlineData("America/New_York", "2026-03-08T03:00:00", "2026-03-08T07:00:00")]
    [InlineData("Europe/Berlin", "2026-03-29T01:59:59.9999999", "2026-03-29T00:59:59.9999999")]
    [InlineData("Europe/Berlin", "2026-03-29T03:00:00", "2026-03-29T01:00:00")]
    [InlineData("Australia/Lord_Howe", "2026-10-04T01:59:59.9999999", "2026-10-03T15:29:59.9999999")]
    [InlineData("Australia/Lord_Howe", "2026-10-04T02:30:00", "2026-10-03T15:30:00")]
    public void LocalToUtc_keepsTheOffsetInForceAtEitherEdgeOfAGap(string zoneId, string local, string expectedUtc)
    {
        var result = TimeZoneHelper.LocalToUtc(DateTime.Parse(local), TimeZoneInfo.FindSystemTimeZoneById(zoneId));

        result.Should().Be(DateTime.Parse(expectedUtc));
    }

    [Theory]
    [InlineData("America/New_York", "2026-11-01T01:00:00", "2026-11-01T05:00:00")]
    [InlineData("America/New_York", "2026-11-01T01:30:00", "2026-11-01T05:30:00")]
    [InlineData("America/New_York", "2026-11-01T02:00:00", "2026-11-01T07:00:00")]
    [InlineData("Europe/Berlin", "2026-10-25T02:00:00", "2026-10-25T00:00:00")]
    [InlineData("Europe/Berlin", "2026-10-25T02:30:00", "2026-10-25T00:30:00")]
    [InlineData("Europe/Berlin", "2026-10-25T03:00:00", "2026-10-25T02:00:00")]
    [InlineData("Australia/Lord_Howe", "2026-04-05T01:30:00", "2026-04-04T14:30:00")]
    [InlineData("Australia/Lord_Howe", "2026-04-05T01:45:00", "2026-04-04T14:45:00")]
    [InlineData("Australia/Lord_Howe", "2026-04-05T02:00:00", "2026-04-04T15:30:00")]
    public void LocalToUtc_mapsAnAmbiguousTimeToItsEarlierInstant(string zoneId, string local, string expectedUtc)
    {
        var result = TimeZoneHelper.LocalToUtc(DateTime.Parse(local), TimeZoneInfo.FindSystemTimeZoneById(zoneId));

        result.Should().Be(DateTime.Parse(expectedUtc));
    }

    [Theory]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Local)]
    public void LocalToUtc_readsTheClockWhateverTheKind(DateTimeKind kind)
    {
        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        var result = TimeZoneHelper.LocalToUtc(new DateTime(2026, 1, 15, 12, 0, 0, kind), newYork);

        result.Should().Be(new DateTime(2026, 1, 15, 17, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void LocalToUtc_isTheIdentityInUtc()
    {
        var local = new DateTime(2026, 3, 8, 2, 30, 0);

        TimeZoneHelper.LocalToUtc(local, TimeZoneInfo.Utc).Should().Be(DateTime.SpecifyKind(local, DateTimeKind.Utc));
    }

    // A system zone whose ID contains letters (so toggling case is meaningful). "UTC" qualifies but
    // round-trips to itself; pick one with a region/word so the mis-cased form actually differs.
    private static TimeZoneInfo FirstAlphabeticSystemZone() =>
        TimeZoneInfo.GetSystemTimeZones().First(z => z.Id.Any(char.IsLetter) && ToggleCase(z.Id) != z.Id);

    private static string ToggleCase(string s)
    {
        var upper = s.ToUpperInvariant();
        return upper == s ? s.ToLowerInvariant() : upper;
    }
}
