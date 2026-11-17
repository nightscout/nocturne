using Nocturne.Core.Clinical.LoopAnalysis;
using Nocturne.Core.Models.LoopAnalysis;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Clinical.Tests.LoopAnalysis;

public class UnannouncedRisesTests
{
    private const long Minute = 60_000;
    private static readonly long T0 = new DateTimeOffset(2024, 6, 10, 2, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    /// <summary>Readings every <paramref name="step"/> minutes, valued by minutes since <see cref="T0"/>.</summary>
    private static List<GlucoseSample> Trace(long fromMinute, long toMinute, Func<double, double> mgdl, int step = 5)
    {
        var readings = new List<GlucoseSample>();
        for (var m = fromMinute; m < toMinute; m += step)
            readings.Add(new GlucoseSample(T0 + m * Minute, mgdl(m)));
        return readings;
    }

    /// <summary>Flat at 110 until T0, then climbing <paramref name="rate"/> mg/dL a minute to 200.</summary>
    private static List<GlucoseSample> Meal(double rate = 3) =>
        Trace(-90, 240, m => m <= 0 ? 110 : Math.Min(200, 110 + rate * m));

    private static readonly long From = T0 - 25 * 60 * Minute;
    private static readonly long To = T0 + 6 * 60 * Minute;

    /// <summary>
    /// A 1 U/h schedule over the day before T0 and after it, or <paramref name="schedule"/>, delivered
    /// as scheduled except where <paramref name="temps"/> run.
    /// </summary>
    private static NetInsulinAboveSchedule Insulin(
        IEnumerable<InsulinDose>? doses = null,
        IEnumerable<DeliveredBasalSegment>? temps = null,
        IReadOnlyList<RateSegment>? schedule = null)
    {
        schedule ??= [new(From, To, 1.0)];
        return new NetInsulinAboveSchedule(
            temps ?? schedule.Select(s => new DeliveredBasalSegment(s.Start, s.End, s.Rate, BasalDeliveryOrigin.Algorithm)),
            schedule,
            doses ?? []);
    }

    private static RiseSearch Search(
        List<GlucoseSample> glucose,
        IntervalSet? exclusions = null,
        IReadOnlyList<CarbEvent>? carbs = null,
        NetInsulinAboveSchedule? insulin = null,
        bool requireCorroboration = false,
        Func<long, double?>? carbsOnBoard = null) =>
        UnannouncedRises.Find(
            glucose, exclusions ?? IntervalSet.Empty, carbs ?? [], insulin ?? Insulin(), requireCorroboration, carbsOnBoard);

    private static List<EventAnchor> Find(
        List<GlucoseSample> glucose,
        IntervalSet? exclusions = null,
        IReadOnlyList<CarbEvent>? carbs = null,
        NetInsulinAboveSchedule? insulin = null,
        bool requireCorroboration = false) =>
        Search(glucose, exclusions, carbs, insulin, requireCorroboration).Anchors;

    private static long At(long minute) => T0 + minute * Minute;

    [Fact]
    public void AnUnannouncedMeal_IsAnchoredWhereTheClimbLeavesTheFloor()
    {
        Find(Meal()).Should().ContainSingle().Which.Should().Be(new EventAnchor(T0, EventAnchorKind.UnannouncedRise));
    }

    [Fact]
    public void ADipBeforeTheClimb_DoesNotMoveTheAnchorBackToIt()
    {
        var glucose = Meal(2).Select(g => g.Mills == At(-25) ? g with { Mgdl = 104 } : g).ToList();

        Find(glucose).Should().ContainSingle().Which.Mills.Should().Be(T0);
    }

    [Fact]
    public void AlternatingNoise_StillFindsTheMealAtItsStart()
    {
        var glucose = Trace(-90, 240, m =>
            (m <= 0 ? 110 : Math.Min(200, 110 + 2 * m)) + ((m / 5) % 2 == 0 ? 3 : -3));

        Find(glucose).Should().ContainSingle().Which.Mills.Should().BeInRange(At(-5), T0);
    }

    [Fact]
    public void ASlowMeal_AtOneMgdlAMinute_IsFound()
    {
        Find(Meal(1)).Should().ContainSingle().Which.Mills
            .Should().BeInRange(T0, At(5), "at 1 mg/dL a minute the first reading of the climb is still within the floor's tolerance");
    }

    [Fact]
    public void ARiseSlowerThanTheWindow_IsNotAMeal()
    {
        Find(Meal(0.7)).Should().BeEmpty();
    }

    [Fact]
    public void ARiseAnsweredByAutomaticBolusesLate_IsCorroborated()
    {
        var smbs = new[] { 15, 20, 25, 30 }.Select(m => new InsulinDose(At(m), 0.2, true)).ToList();

        Find(Meal(), insulin: Insulin(smbs), requireCorroboration: true).Should().ContainSingle();
    }

    [Fact]
    public void ARiseAnsweredByTempBasalsAlone_IsCorroborated()
    {
        var temps = new List<DeliveredBasalSegment>
        {
            new(From, At(5), 1.0, BasalDeliveryOrigin.Algorithm),
            new(At(5), At(50), 2.5, BasalDeliveryOrigin.Algorithm),
            new(At(50), At(360), 1.0, BasalDeliveryOrigin.Algorithm),
        };

        Find(Meal(), insulin: Insulin(temps: temps), requireCorroboration: true)
            .Should().ContainSingle("1.5 U/h above a 1 U/h schedule for 40 minutes is a full hour's half");
    }

    [Fact]
    public void ASteepDawnRise_FailsCorroboration()
    {
        // At the steep end of the dawn phenomenon, 1 mg/dL a minute, glucose alone reads it as a
        // rise; the loop answers it with a trickle well short of half an hour's schedule.
        var dawn = Trace(-60, 180, m => m <= 0 ? 120 : Math.Min(180, 120 + m));
        var trickle = Enumerable.Range(0, 6).Select(i => new InsulinDose(At(i * 5), 0.05, true)).ToList();

        Find(dawn, insulin: Insulin(trickle)).Should().ContainSingle();
        Find(dawn, insulin: Insulin(trickle), requireCorroboration: true).Should().BeEmpty();
    }

    [Fact]
    public void ACompressionLowRecovery_NeverQualifies()
    {
        // Steady at 120, a false low at 60 for 35 minutes, then straight back to 120.
        var glucose = Trace(-60, 120, m => m is >= 0 and < 35 ? 60 : 120);

        Find(glucose, exclusions: IntervalSet.Of(T0, At(35))).Should().BeEmpty("the low is excluded, so the climb has no stable start");
        Find(glucose).Should().BeEmpty("unexcluded, its floor is below 70 and its climb faster than glucose moves");
    }

    [Fact]
    public void AHypoRebound_IsNotAMeal()
    {
        Find(Trace(-60, 120, m => m <= 0 ? 62 : Math.Min(150, 62 + 2 * m))).Should().BeEmpty();
    }

    [Fact]
    public void AMealThatStallsAndClimbsAgain_IsOneRise()
    {
        var glucose = Trace(-60, 300, m => m switch
        {
            <= 0 => 110,
            <= 20 => 110 + 3 * m,
            <= 70 => 170,
            _ => Math.Min(240, 170 + 3 * (m - 70)),
        });

        Find(glucose).Should().ContainSingle().Which.Mills.Should().Be(T0);
    }

    [Fact]
    public void AFloorSetComingDownFromAPeak_DoesNotStandHoursLater()
    {
        // Coming down from 190 to 110 over two hours, resting at 110 for four, then a meal. The
        // stretch near the end of the fall reads a floor near 128; a reading of 125 four hours later
        // must not count as resting at it.
        var glucose = Trace(-360, 120, m => m switch
        {
            < -240 => 190 - 80 * (m + 360) / 120.0,
            <= 0 => 110,
            _ => Math.Min(200, 110 + 3 * m),
        });

        Find(glucose).Should().ContainSingle(a => a.Mills > At(-60)).Which.Mills.Should().Be(T0);
    }

    [Fact]
    public void ARise_WithCarbsTwentyMinutesLater_IsNotAnchored()
    {
        Find(Meal(), carbs: [new CarbEvent(At(20), 45)]).Should().BeEmpty();
        Find(Meal(), carbs: [new CarbEvent(At(31), 45)])
            .Should().ContainSingle("a carb entry over half an hour away does not announce it");
    }

    [Fact]
    public void ARiseFromAFall_HasNoStableStart()
    {
        Find(Trace(-60, 120, m => m <= 0 ? 110 - m : 110 + 3 * m)).Should().BeEmpty();
    }

    [Fact]
    public void ARiseAcrossAGap_IsNotReadAcrossIt()
    {
        Find(Meal().Where(g => g.Mills <= T0 || g.Mills > At(20)).ToList()).Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AMeal_OnOneMinuteDataWithNoise_IsFound(int seed)
    {
        var noise = new Random(seed);
        var glucose = Trace(-120, 120, m => (m <= 0 ? 110 : Math.Min(230, 110 + 2.0 * m)) + noise.NextDouble() * 8 - 4, step: 1);

        Find(glucose).Should().ContainSingle().Which.Mills.Should().BeInRange(At(-10), At(5));
    }

    [Fact]
    public void AMeal_WithOneJitteredReading_IsFound()
    {
        var glucose = Trace(-120, 120, m => (m <= 0 ? 110 : Math.Min(230, 110 + 2.0 * m)) + (m == 15 ? 16 : 0));

        Find(glucose).Should().ContainSingle().Which.Mills.Should().Be(T0);
    }

    [Fact]
    public void AJumpThatHolds_IsAnArtefactOverAQuarterHour()
    {
        Find(Trace(-120, 120, m => m <= 0 ? 110 : 200)).Should().BeEmpty("90 mg/dL in five minutes is 6 a minute over 15");
    }

    [Fact]
    public void CarbsLoggedBeforeTheClimb_AnnounceIt()
    {
        Find(Meal(2), carbs: [new CarbEvent(At(-40), 50)]).Should().BeEmpty();
        Find(Meal(2), carbs: [new CarbEvent(At(-181), 50)]).Should().ContainSingle("three hours on, a meal no longer explains a climb");
    }

    [Fact]
    public void ATwoStepMeal_IsAnnouncedByItsCarbs_AndBothStepsAreItsOwn()
    {
        var glucose = Trace(-120, 300, m => m switch
        {
            <= 0 => 110,
            <= 25 => 110 + 2 * m,
            <= 90 => 160,
            _ => Math.Min(260, 160 + 1.5 * (m - 90)),
        });

        var search = Search(glucose, carbs: [new CarbEvent(T0, 50)]);

        search.Anchors.Should().BeEmpty();
        search.Climbs.Should().BeEmpty("the meal caused the first step, and the second is merged into it");
    }

    [Fact]
    public void CarbsOnBoardAtTheAnchor_AnnounceTheClimb()
    {
        Search(Meal(), carbsOnBoard: m => m == T0 ? 12 : 0).Anchors.Should().BeEmpty();
        Search(Meal(), carbsOnBoard: _ => 0).Anchors.Should().ContainSingle();
    }

    [Fact]
    public void ARiseInAZeroBasalHour_IsCorroboratedAgainstTheDaysMeanRate()
    {
        // 1 U/h all day but 0 U/h for the hour the rise starts in: the bar is half an hour of the
        // day's mean, about 0.48 U, not zero.
        var schedule = new List<RateSegment> { new(From, At(-30), 1.0), new(At(-30), At(30), 0), new(At(30), To, 1.0) };

        Find(Meal(), insulin: Insulin([new(At(10), 0.3, true)], schedule: schedule), requireCorroboration: true)
            .Should().BeEmpty("0.3 U is short of half an hour of the day's schedule");
        Find(Meal(), insulin: Insulin([new(At(10), 0.6, true)], schedule: schedule), requireCorroboration: true)
            .Should().ContainSingle();
    }

    [Fact]
    public void ALowBasalDay_IsHeldToTheAbsoluteFloor()
    {
        var schedule = new List<RateSegment> { new(From, To, 0.05) };

        Find(Meal(), insulin: Insulin([new(At(10), 0.05, true)], schedule: schedule), requireCorroboration: true)
            .Should().BeEmpty("one 0.05 U bolus is not an answer to a meal");
        Find(Meal(), insulin: Insulin([new(At(10), 0.1, true), new(At(20), 0.15, true)], schedule: schedule), requireCorroboration: true)
            .Should().ContainSingle();
    }

    [Fact]
    public void ARiseAcrossAScheduleChange_IsHeldToTheSameBar()
    {
        // The schedule steps from 1 to 2 U/h fifteen minutes after the rise starts; the bar is the
        // day's mean, so where the step falls does not move it.
        var schedule = new List<RateSegment> { new(From, At(15), 1.0), new(At(15), To, 2.0) };

        Find(Meal(), insulin: Insulin([new(At(10), 0.6, true)], schedule: schedule), requireCorroboration: true)
            .Should().ContainSingle();
    }

    [Fact]
    public void AClimbMergedIntoAnEarlierOne_IsPartOfIt()
    {
        var glucose = Trace(-60, 300, m => m switch
        {
            <= 0 => 110,
            <= 20 => 110 + 3 * m,
            <= 70 => 170,
            _ => Math.Min(240, 170 + 3 * (m - 70)),
        });

        var search = Search(glucose);

        search.Anchors.Should().ContainSingle().Which.Mills.Should().Be(T0);
        search.Climbs.Should().Equal(T0);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(150)]
    public void ASnackAfterAMealThatDidNotClimb_IsAFurtherIntake(int snackMinute)
    {
        var glucose = Trace(-120, snackMinute + 120, m => m <= snackMinute ? 110 : Math.Min(200, 110 + 2 * (m - snackMinute)));

        var search = Search(glucose, carbs: [new CarbEvent(T0, 50)]);

        search.Anchors.Should().BeEmpty("the meal's carbs still announce the snack's climb");
        search.Climbs.Should().Equal(At(snackMinute));
    }

    [Theory]
    [InlineData(new double[] { 135 })]
    [InlineData(new double[] { 110, 135 })]
    [InlineData(new double[] { 100, 118, 135 })]
    public void AShallowFalseLowSteppingBack_IsNotARise(double[] steps)
    {
        // Steady at 135, a false low at 85 for 40 minutes, then back in one or two readings.
        var glucose = Trace(-120, 120, m => m switch
        {
            >= -40 and < 0 => 85,
            >= 0 when m / 5 < steps.Length => steps[(int)m / 5],
            _ => 135,
        });

        Find(glucose).Should().BeEmpty();
    }

    [Theory]
    [InlineData(4.0, 5)]
    [InlineData(4.5, 5)]
    [InlineData(4.0, 1)]
    [InlineData(4.5, 1)]
    public void AFastMeal_IsFound(double rate, int step)
    {
        var glucose = Trace(-120, 120, m => m <= 0 ? 110 : Math.Min(250, 110 + rate * m), step);

        Find(glucose).Should().ContainSingle().Which.Mills.Should().BeInRange(T0, At(5));
    }

    [Fact]
    public void AMealAfterACorrectionBroughtGlucoseDownGradually_IsFound()
    {
        // From 180 down to 110 at 2 mg/dL a minute, resting there an hour, then a meal.
        var glucose = Trace(-180, 120, m => m switch
        {
            < -130 => 180,
            < -95 => 180 - 2 * (m + 130),
            <= 0 => 110,
            _ => Math.Min(220, 110 + 3 * m),
        });

        Find(glucose).Should().ContainSingle().Which.Mills.Should().Be(T0);
    }
}
