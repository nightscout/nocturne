using FluentAssertions;
using Nocturne.Connectors.Tandem.EventParser;
using Xunit;

namespace Nocturne.Connectors.Tandem.Tests.EventParser;

public class TandemTimeResolverTests
{
    private const long RawTimestamp2024 = 504921600L; // 2024-01-01T00:00:00 wall clock

    [Fact]
    public void ToUtc_treats_raw_as_utc_when_offset_zero()
    {
        var resolver = new TandemTimeResolver(0);

        resolver.ToUtc(RawTimestamp2024).Should().Be(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        resolver.OffsetMinutes.Should().Be(0);
    }

    [Fact]
    public void ToUtc_subtracts_positive_offset_to_recover_utc()
    {
        // A user at UTC+10 whose pump reads 00:00 local is actually at 14:00 UTC the previous day.
        var resolver = new TandemTimeResolver(10);

        resolver.ToUtc(RawTimestamp2024).Should().Be(new DateTime(2023, 12, 31, 14, 0, 0, DateTimeKind.Utc));
        resolver.OffsetMinutes.Should().Be(600);
    }

    [Fact]
    public void ToUtc_adds_for_negative_offset()
    {
        var resolver = new TandemTimeResolver(-5);

        resolver.ToUtc(RawTimestamp2024).Should().Be(new DateTime(2024, 1, 1, 5, 0, 0, DateTimeKind.Utc));
        resolver.OffsetMinutes.Should().Be(-300);
    }
}
