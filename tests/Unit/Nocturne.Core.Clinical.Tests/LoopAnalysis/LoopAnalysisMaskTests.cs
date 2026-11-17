using Nocturne.Core.Clinical.LoopAnalysis;
using Nocturne.Core.Models;
using Nocturne.Core.Models.LoopAnalysis;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Clinical.Tests.LoopAnalysis;

public class LoopAnalysisMaskTests
{
    private static readonly TimeZoneInfo Sydney = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");

    [Fact]
    public void IntervalSet_MergesSubtractsAndIntersectsHalfOpen()
    {
        var set = IntervalSet.Of([new(0, 10), new(10, 20), new(30, 40)]);
        set.Intervals.Should().Equal(new MillsInterval(0, 20), new MillsInterval(30, 40));
        set.Contains(20).Should().BeFalse();
        set.Contains(19).Should().BeTrue();

        set.Subtract(IntervalSet.Of(5, 35)).Intervals.Should().Equal(new MillsInterval(0, 5), new MillsInterval(35, 40));
        set.Intersect(IntervalSet.Of(15, 32)).Intervals.Should().Equal(new MillsInterval(15, 20), new MillsInterval(30, 32));

        var gaps = new List<MillsInterval>();
        set.ForEachGap(-5, 45, (s, e) => gaps.Add(new(s, e)));
        gaps.Should().Equal(new MillsInterval(-5, 0), new MillsInterval(20, 30), new MillsInterval(40, 45));
    }

    [Fact]
    public void DefaultFilter_KeepsOverridesAndTempTargetsButMasksIllnessAndOpenLoop()
    {
        var sources = new LoopAnalysisMaskSources
        {
            Overrides = [new(0, 10)],
            TemporaryTargets = [new(10, 20)],
            Illness = [new(20, 30)],
            OpenLoop = [new(30, 40)],
            DataExclusions = [new(50, 60)],
        };

        var mask = LoopAnalysisMask.Build(new LoopAnalysisMasks(), 0, 100, TimeZoneInfo.Utc, sources);

        mask.Analysed.Intervals.Should().Equal(new MillsInterval(0, 20), new MillsInterval(40, 100));
        mask.Glucose.Intervals.Should().Equal(new MillsInterval(0, 20), new MillsInterval(40, 50), new MillsInterval(60, 100));
    }

    [Fact]
    public void ProfileFilter_KeepsOnlyThatProfilesTime()
    {
        var sources = new LoopAnalysisMaskSources
        {
            Profiles = [new("Default", 0, 50), new("Weekend", 50, 100)],
        };

        var mask = LoopAnalysisMask.Build(new LoopAnalysisMasks { ProfileName = "Weekend" }, 0, 100, TimeZoneInfo.Utc, sources);

        mask.Analysed.Intervals.Should().Equal(new MillsInterval(50, 100));
    }

    [Fact]
    public void OpenLoop_TakesManualModesAndNonAidPumpsExceptWhereAidEvidenceExists()
    {
        const long Minute = 60_000;
        var openLoop = LoopAnalysisMask.OpenLoop(
            manualPumpModes: [new(0, 10 * Minute)],
            nonAidPumps: [new(0, 300 * Minute)],
            aidPumps: [new(200 * Minute, 300 * Minute)],
            apsSnapshots: [new(100 * Minute, LoopClosed: true)]);

        openLoop.Intervals.Should().Equal(
            new MillsInterval(0, 100 * Minute),
            new MillsInterval(130 * Minute, 200 * Minute));
    }

    [Fact]
    public void OpenLoop_TakesASnapshotWithTheLoopOpen_EvenOnAnAidPump_UntilOneWithItClosed()
    {
        const long Minute = 60_000;
        var openLoop = LoopAnalysisMask.OpenLoop(
            manualPumpModes: [],
            nonAidPumps: [],
            aidPumps: [new(0, 300 * Minute)],
            apsSnapshots:
            [
                new(100 * Minute, LoopClosed: false),
                new(105 * Minute, LoopClosed: false),
                new(110 * Minute, LoopClosed: true),
                new(200 * Minute, LoopClosed: false),
            ]);

        openLoop.Intervals.Should().Equal(
            new MillsInterval(100 * Minute, 110 * Minute),
            new MillsInterval(200 * Minute, 230 * Minute));
    }

    [Fact]
    public void OpenLoop_KeepsTheClosedSnapshot_OfTwoAtOneInstant()
    {
        const long Minute = 60_000;
        var openLoop = LoopAnalysisMask.OpenLoop(
            manualPumpModes: [],
            nonAidPumps: [new(0, 300 * Minute)],
            aidPumps: [],
            apsSnapshots: [new(100 * Minute, LoopClosed: false), new(100 * Minute, LoopClosed: true)]);

        openLoop.Intervals.Should().Equal(
            new MillsInterval(0, 100 * Minute),
            new MillsInterval(130 * Minute, 300 * Minute));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public void OpenLoop_TakesAQuietClosedCycle_AsAidEvidence(bool? closedLoop)
    {
        const long Minute = 60_000;
        var quiet = new ApsLoopCycle
        {
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(100 * Minute).UtcDateTime,
            AidAlgorithm = AidAlgorithm.Loop,
            Enacted = false,
            ClosedLoop = closedLoop,
        };

        var openLoop = LoopAnalysisMask.OpenLoop(
            manualPumpModes: [],
            nonAidPumps: [new(0, 300 * Minute)],
            aidPumps: [],
            apsSnapshots: [new(quiet.Mills, quiet.LoopClosed)]);

        quiet.EnactedClosedLoop.Should().BeFalse("AID active % credits only an enacted cycle");
        openLoop.Intervals.Should().Equal(
            new MillsInterval(0, 100 * Minute),
            new MillsInterval(130 * Minute, 300 * Minute));
    }

    [Fact]
    public void StepTimeline_LatestStartedWins()
    {
        var timeline = StepTimeline<int>.LatestStartedWins([new(0, 100, 1), new(20, 40, 2), new(30, 120, 3)]);

        timeline.Steps.Should().Equal(new Step<int>(0, 20, 1), new Step<int>(20, 30, 2), new Step<int>(30, 120, 3));
        timeline.TryAt(25, out var value).Should().BeTrue();
        value.Should().Be(2);
        timeline.TryAt(120, out _).Should().BeFalse();
    }

    [Fact]
    public void DayStart_FollowsTheZonesMidnightAcrossDst()
    {
        LocalSlotBinning.DayStartMills(new DateOnly(2024, 4, 7), Sydney)
            .Should().Be(new DateTimeOffset(2024, 4, 6, 13, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds());
        LocalSlotBinning.DayStartMills(new DateOnly(2024, 4, 8), Sydney)
            .Should().Be(new DateTimeOffset(2024, 4, 7, 14, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds());
    }
}
