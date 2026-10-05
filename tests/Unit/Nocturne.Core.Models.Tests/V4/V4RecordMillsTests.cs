using FluentAssertions;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Core.Models.Tests.V4;

/// <summary>
/// <see cref="V4RecordBase.Mills"/> converts a UTC or unspecified timestamp by arithmetic; it must
/// agree with <see cref="DateTimeOffset.ToUnixTimeMilliseconds"/> to the millisecond, including
/// before the epoch and below a millisecond, where integer division could round the other way.
/// </summary>
[Trait("Category", "Unit")]
public class V4RecordMillsTests
{
    public static TheoryData<DateTime> Timestamps => new()
    {
        DateTime.UnixEpoch,
        new DateTime(2026, 6, 1, 12, 30, 15, 123, DateTimeKind.Utc).AddTicks(9_999),
        new DateTime(1969, 12, 31, 23, 59, 59, 999, DateTimeKind.Utc).AddTicks(5_000),
        new DateTime(1901, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(1),
        DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc),
        DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc),
        new DateTime(2026, 6, 1, 12, 30, 15, 123, DateTimeKind.Unspecified).AddTicks(1),
        new DateTime(1960, 3, 4, 5, 6, 7, 8, DateTimeKind.Unspecified).AddTicks(4_321),
    };

    [Theory]
    [MemberData(nameof(Timestamps))]
    public void Mills_MatchesDateTimeOffset(DateTime timestamp)
    {
        var record = new SensorGlucose { Timestamp = timestamp };

        record.Mills.Should().Be(new DateTimeOffset(timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds());
    }
}
