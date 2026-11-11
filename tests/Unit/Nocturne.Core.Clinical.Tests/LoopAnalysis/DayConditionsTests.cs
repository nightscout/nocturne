using Nocturne.Core.Clinical.LoopAnalysis;
using Nocturne.Core.Models.LoopAnalysis;

namespace Nocturne.Core.Clinical.Tests.LoopAnalysis;

public class DayConditionsTests
{
    private static readonly TimeZoneInfo Sydney = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");
    private const long Minute = 60_000;
    private const long Hour = 60 * Minute;

    private static long Local(int d, int h, int mi = 0) =>
        new DateTimeOffset(new DateTime(2024, 6, d, h, mi, 0), TimeSpan.FromHours(10)).ToUnixTimeMilliseconds();

    private static readonly DateOnly First = new(2024, 6, 10);
    private static readonly long[] Starts = [.. Enumerable.Range(10, 5).Select(d => Local(d, 0))];

    /// <summary>Four days, 2024-06-10 to 13.</summary>
    private static DaySelection Select(
        DayCondition condition,
        Func<long, double>? glucose = null,
        IEnumerable<CarbEvent>? carbs = null,
        IEnumerable<MillsInterval>? exercise = null,
        long? visibleFrom = null)
    {
        var readings = new List<GlucoseSample>();
        for (var t = Starts[0]; t < Starts[^1]; t += 5 * Minute)
            readings.Add(new GlucoseSample(t, (glucose ?? (_ => 120))(t)));
        return DayConditions.Select(condition, First, Starts, Sydney, readings, carbs ?? [], exercise ?? [], [], visibleFrom);
    }

    [Fact]
    public void ADayWithAnExerciseSpan_IsKept_AndAllOtherDaysAreTheRest()
    {
        MillsInterval[] exercise = [new(Local(11, 17), Local(11, 18)), new(Local(12, 23, 30), Local(13, 0, 30))];

        var with = Select(new DayCondition { Kind = DayConditionKind.Exercise }, exercise: exercise);
        var without = Select(new DayCondition { Kind = DayConditionKind.Exercise, Others = true }, exercise: exercise);

        with.Kept.Should().Equal(new DateOnly(2024, 6, 11), new DateOnly(2024, 6, 12), new DateOnly(2024, 6, 13));
        without.Kept.Should().Equal(new DateOnly(2024, 6, 10));
        (with.Matching, with.NotMatching, with.Undecided).Should().Be((3, 1, 0), "a span across midnight belongs to both days");
    }

    [Fact]
    public void ALow_IsALowOfTheGlucoseStatistics_AndOvernightIsMidnightTo0600()
    {
        var nightLow = Local(10, 3);
        var dayLow = Local(11, 15);
        var dip = Local(12, 4);
        double Glucose(long t) =>
            t >= nightLow && t < nightLow + 30 * Minute || t >= dayLow && t < dayLow + 30 * Minute || t >= dip && t < dip + 10 * Minute ? 60 : 120;

        Select(new DayCondition { Kind = DayConditionKind.Low }, Glucose).Kept
            .Should().Equal(new DateOnly(2024, 6, 10), new DateOnly(2024, 6, 11));
        Select(new DayCondition { Kind = DayConditionKind.LowOvernight }, Glucose).Kept
            .Should().ContainSingle().Which.Should().Be(new DateOnly(2024, 6, 10), "the afternoon low is not overnight and a 10-minute dip is no low");
    }

    [Theory]
    [InlineData(150.0, null, new[] { 11, 12 })]
    [InlineData(null, 149.0, new[] { 10, 13 })]
    [InlineData(100.0, 200.0, new[] { 11, 12 })]
    public void TheDaysCarbs_AreCountedOverTheLocalDay_InclusiveBounds(double? min, double? max, int[] kept)
    {
        CarbEvent[] carbs = [new(Local(10, 8), 60), new(Local(11, 8), 100), new(Local(11, 23, 59), 50), new(Local(12, 12), 200), new(Local(13, 0, 1), 20)];

        Select(new DayCondition { Kind = DayConditionKind.Carbs, MinCarbs = min, MaxCarbs = max }, carbs: carbs)
            .Kept.Select(d => d.Day).Should().Equal(kept);
    }

    [Fact]
    public void ADayTheReaderCannotSeeWhole_IsInNeitherGroup()
    {
        var visibleFrom = Local(12, 9);

        var with = Select(new DayCondition { Kind = DayConditionKind.Exercise }, exercise: [new(Local(10, 9), Local(10, 10))], visibleFrom: visibleFrom);
        var without = Select(new DayCondition { Kind = DayConditionKind.Exercise, Others = true }, visibleFrom: visibleFrom);

        with.Kept.Should().BeEmpty();
        with.Undecided.Should().Be(3);
        without.Kept.Should().Equal(new DateOnly(2024, 6, 13));
    }

    [Fact]
    public void TheKeptDays_AreWholeLocalDaysAsInstants()
    {
        var selection = new DaySelection([new DateOnly(2024, 6, 11), new DateOnly(2024, 6, 13)], 2, 2, 0);

        var instants = DayConditions.Instants(selection, First, Starts);

        instants.Contains(Local(11, 0)).Should().BeTrue();
        instants.Contains(Local(11, 23, 59)).Should().BeTrue();
        instants.Contains(Local(12, 12)).Should().BeFalse();
        instants.Contains(Local(13, 12)).Should().BeTrue();
    }
}
