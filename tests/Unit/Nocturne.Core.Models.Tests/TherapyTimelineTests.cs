using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.Core.Models.Tests;

public class TherapyTimelineTests
{
    private const long Minute = 60 * 1000;
    private const int Segments = 1000;

    // Segment i covers [i, i + 1) minutes and carries DIA i + 1, so a read names its segment.
    private static TherapyTimeline ManySegments() => new(
        [.. Enumerable.Range(0, Segments).Select(i => new TherapySegment(
            i * Minute, (i + 1) * Minute,
            new TherapySnapshot(i + 1, 30, null, null, 0, null, null, null)))]);

    [Fact]
    public void ReadsBackwards_FindTheSegmentHoldingEachInstant()
    {
        var timeline = ManySegments();
        timeline.SnapshotAt(Segments * Minute - 1);

        for (var i = Segments - 1; i >= 0; i--)
        {
            Assert.Equal(i + 1, timeline.SnapshotAt(i * Minute).Dia);
            Assert.Equal(i + 1, timeline.SnapshotAt(i * Minute + Minute / 2).Dia);
            Assert.Equal(i + 1, timeline.SnapshotAt((i + 1) * Minute - 1).Dia);
        }
    }

    [Fact]
    public void ReadsOutsideTheTimeline_ClampToTheNearestEnd()
    {
        var timeline = ManySegments();
        timeline.SnapshotAt(Segments * Minute - 1);

        Assert.Equal(1, timeline.SnapshotAt(-Minute).Dia);
        Assert.Equal(Segments, timeline.SnapshotAt(Segments * Minute + Minute).Dia);
    }
}
