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
