using FluentAssertions;
using Nocturne.API.Services.V4;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Services.V4;

public class ActivityNormalizeMillsTests
{
    private static readonly DateTimeOffset ReceivedAt = new(2026, 10, 29, 3, 4, 5, 678, TimeSpan.Zero);

    [Fact]
    public void An_activity_with_no_time_is_dated_and_created_at_receipt()
    {
        var activity = new Activity { Type = "Exercise" };

        ActivityDecomposer.NormalizeMills(activity, ReceivedAt);

        activity.Mills.Should().Be(ReceivedAt.ToUnixTimeMilliseconds());
        activity.CreatedAt.Should().Be("2026-10-29T03:04:05.678Z");
    }

    [Fact]
    public void An_activity_with_a_created_at_keeps_it()
    {
        var activity = new Activity { Type = "Exercise", CreatedAt = "2026-10-01T10:00:00.000Z" };

        ActivityDecomposer.NormalizeMills(activity, ReceivedAt);

        activity.Mills.Should().Be(new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds());
        activity.CreatedAt.Should().Be("2026-10-01T10:00:00.000Z");
    }
}
