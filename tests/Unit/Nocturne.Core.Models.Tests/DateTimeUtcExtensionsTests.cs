using FluentAssertions;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.Core.Models.Tests;

[Trait("Category", "Unit")]
public class DateTimeUtcExtensionsTests
{
    private const long Instant = 1_790_000_000_000;

    private static readonly DateTime UtcValue = DateTimeOffset.FromUnixTimeMilliseconds(Instant).UtcDateTime;

    [Fact]
    public void ToMills_ReadsUnspecifiedAsUtc()
    {
        DateTime.SpecifyKind(UtcValue, DateTimeKind.Unspecified).ToMills().Should().Be(Instant);
    }

    [Fact]
    public void ToMills_KeepsUtcInstant()
    {
        UtcValue.ToMills().Should().Be(Instant);
    }

    [Fact]
    public void ToMills_ConvertsLocalToTheSameInstant()
    {
        UtcValue.ToLocalTime().ToMills().Should().Be(Instant);
    }

    [Fact]
    public void ToUtcDateTime_RoundTripsWithToMills()
    {
        Instant.ToUtcDateTime().ToMills().Should().Be(Instant);
    }

    [Fact]
    public void ToUtcDateTime_IsUtcKind()
    {
        Instant.ToUtcDateTime().Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void ToUtcDateTime_ReadsZeroAsTheEpoch()
    {
        0L.ToUtcDateTime().Should().Be(DateTime.UnixEpoch);
    }

    [Fact]
    public void ToUtcDateTime_ReadsNegativeBeforeTheEpoch()
    {
        (-1L).ToUtcDateTime().Should().Be(DateTime.UnixEpoch.AddMilliseconds(-1));
    }

    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void ToUtcDateTime_ThrowsOutsideTheRepresentableRange(long mills)
    {
        var act = () => mills.ToUtcDateTime();

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
