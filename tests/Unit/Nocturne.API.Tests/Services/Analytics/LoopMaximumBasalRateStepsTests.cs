using FluentAssertions;
using Nocturne.API.Services.Analytics;
using Nocturne.Core.Clinical.LoopAnalysis;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Analytics;

public class LoopMaximumBasalRateStepsTests
{
    private const long Day = 86_400_000;

    private static TherapySettings Version(long mills, double? maximum) => new()
    {
        Id = Guid.NewGuid(),
        ProfileName = "Default",
        Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(mills).UtcDateTime,
        LoopSettings = maximum is null ? null : new LoopProfileSettings { MaximumBasalRatePerHour = maximum },
    };

    private static Step<double>[] Steps(long from, long to, params TherapySettings[] newestFirst) =>
        [.. LoopAnalysisService.MaximumBasalRateSteps(
            [new ProfileSpan("Default", from, to, null)],
            new Dictionary<string, TherapySettings[]> { ["Default"] = newestFirst },
            to).Steps];

    [Fact]
    public void ASpanStartingBeforeEveryVersionReadsTheNewest()
    {
        var steps = Steps(0, 10 * Day, Version(5 * Day, 4), Version(2 * Day, 2));

        steps.Should().Equal(
            new Step<double>(0, 2 * Day, 4),
            new Step<double>(2 * Day, 5 * Day, 2),
            new Step<double>(5 * Day, 10 * Day, 4));
    }

    [Fact]
    public void AVersionThatRecordsNoMaximumLeavesItsSliceOut()
    {
        var steps = Steps(3 * Day, 10 * Day, Version(5 * Day, null), Version(2 * Day, 2));

        steps.Should().Equal(new Step<double>(3 * Day, 5 * Day, 2));
    }

    [Fact]
    public void NoVersionsIsNoSteps()
    {
        Steps(0, 10 * Day).Should().BeEmpty();
    }
}
