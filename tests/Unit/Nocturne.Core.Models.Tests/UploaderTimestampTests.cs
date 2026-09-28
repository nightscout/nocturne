using FluentAssertions;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.Core.Models.Tests;

[Trait("Category", "Unit")]
public class UploaderTimestampTests
{
    [Fact]
    public void TryParse_ZSuffixedString_IsUtc()
    {
        UploaderTimestamp.TryParse("2026-09-26T10:00:00.000Z", out var parsed).Should().BeTrue();

        parsed.Should().Be(new DateTimeOffset(2026, 9, 26, 10, 0, 0, TimeSpan.Zero));
        parsed.Offset.Should().Be(TimeSpan.Zero);
    }

    [Theory]
    [InlineData("2026-09-26T10:00:00+05:00", 300)]
    [InlineData("2026-09-26T10:00:00-03:30", -210)]
    public void TryParse_ExplicitOffset_IsKept(string value, int offsetMinutes)
    {
        UploaderTimestamp.TryParse(value, out var parsed).Should().BeTrue();

        parsed.Offset.Should().Be(TimeSpan.FromMinutes(offsetMinutes));
        parsed.UtcDateTime.Should()
            .Be(new DateTime(2026, 9, 26, 10, 0, 0, DateTimeKind.Utc).AddMinutes(-offsetMinutes));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a timestamp")]
    public void TryParse_Unparseable_ReturnsFalse(string? value)
    {
        UploaderTimestamp.TryParse(value, out _).Should().BeFalse();
        UploaderTimestamp.ParseUtcDateTime(value).Should().BeNull();
    }
}
