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
}
