using Moq;
using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Services.Analytics;

internal static class StatisticsServiceMockExtensions
{
    /// <summary>
    /// Has <paramref name="statistics"/> answer <see cref="IStatisticsService.CalculateTimeInRange"/>
    /// as the real service does, for a test of what is built on its figures.
    /// </summary>
    public static Mock<IStatisticsService> WithRealTimeInRange(this Mock<IStatisticsService> statistics)
    {
        var real = new StatisticsService();
        statistics
            .Setup(s => s.CalculateTimeInRange(It.IsAny<IEnumerable<SensorGlucose>>(), It.IsAny<GlycemicThresholds?>()))
            .Returns((IEnumerable<SensorGlucose> entries, GlycemicThresholds? thresholds) =>
                real.CalculateTimeInRange(entries, thresholds));
        return statistics;
    }
}
