using FluentAssertions;
using Nocturne.Connectors.CareLink.Utilities;
using Xunit;

namespace Nocturne.Connectors.CareLink.Tests.Utilities;

public class CareLinkTimestampParserTests
{
    [Fact]
    public void CalculatePumpOffset_ReturnsCorrectOffset_WhenPumpIsAheadOfServer()
    {
        var pumpTimeString = "2024-01-15T14:30:00";
        var serverTimeMs = new DateTimeOffset(2024, 1, 15, 13, 30, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var offsetMs = CareLinkTimestampParser.CalculatePumpOffsetMs(pumpTimeString, serverTimeMs);
        offsetMs.Should().Be(TimeSpan.FromHours(1).TotalMilliseconds);
    }

    [Fact]
    public void CalculatePumpOffset_ReturnsNegativeOffset_WhenPumpIsBehindServer()
    {
        var pumpTimeString = "2024-01-15T08:30:00";
        var serverTimeMs = new DateTimeOffset(2024, 1, 15, 13, 30, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var offsetMs = CareLinkTimestampParser.CalculatePumpOffsetMs(pumpTimeString, serverTimeMs);
        offsetMs.Should().Be(TimeSpan.FromHours(-5).TotalMilliseconds);
    }

    [Fact]
    public void CalculatePumpOffset_RoundsToNearestHour()
    {
        var pumpTimeString = "2024-01-15T14:33:00";
        var serverTimeMs = new DateTimeOffset(2024, 1, 15, 13, 30, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var offsetMs = CareLinkTimestampParser.CalculatePumpOffsetMs(pumpTimeString, serverTimeMs);
        offsetMs.Should().Be(TimeSpan.FromHours(1).TotalMilliseconds);
    }

    [Fact]
    public void ParseSgTimestamp_ConvertsToUtc_WithOffset()
    {
        var sgDatetime = "2024-01-15T14:30:00";
        var pumpOffsetMs = TimeSpan.FromHours(1).TotalMilliseconds;
        var result = CareLinkTimestampParser.ParseSgTimestamp(sgDatetime, pumpOffsetMs);
        result.Should().NotBeNull();
        result!.Value.Should().Be(new DateTime(2024, 1, 15, 13, 30, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void ParseSgTimestamp_ReturnsNull_ForInvalidInput()
    {
        CareLinkTimestampParser.ParseSgTimestamp("not-a-date", 0).Should().BeNull();
    }
}
