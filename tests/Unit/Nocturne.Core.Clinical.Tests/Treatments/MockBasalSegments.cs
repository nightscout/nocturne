using Moq;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Models.Basal;

namespace Nocturne.Core.Clinical.Tests.Treatments;

internal static class MockBasalSegments
{
    /// <summary>A schedule of <paramref name="rate"/> U/h over whatever window it is asked for.</summary>
    public static Mock<IBasalSegmentService> Flat(double rate)
    {
        var segments = new Mock<IBasalSegmentService>();
        segments
            .Setup(s => s.GetSegmentsAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Returns((long from, long to, CancellationToken _) =>
                new[] { new BasalSegment(from, to, rate, rate, "Default") }.ToAsyncEnumerable());
        return segments;
    }
}
