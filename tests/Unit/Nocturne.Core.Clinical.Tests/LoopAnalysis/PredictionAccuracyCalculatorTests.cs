using FluentAssertions;
using Nocturne.Core.Clinical.LoopAnalysis;
using Nocturne.Core.Models.LoopAnalysis;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Core.Clinical.Tests.LoopAnalysis;

public class PredictionAccuracyCalculatorTests
{
    private static readonly TimeZoneInfo Sydney = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");
    private const long Minute = 60_000;
    private const long Hour = 60 * Minute;

    // Local midnight 2024-06-10 in Sydney (UTC+10, no DST in June).
    private static readonly long Midnight = new DateTimeOffset(2024, 6, 9, 14, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
    private static readonly long From = Midnight;
    private static readonly long To = Midnight + 24 * Hour;

    /// <summary>A Loop snapshot as the decomposer stores it: one curve, in <c>predicted_default_json</c>.</summary>
    private static ApsPredictionPoint LoopFixture(long start, string json) => new(
        DateTimeOffset.FromUnixTimeMilliseconds(start).UtcDateTime, "loop://iPhone", true, AidAlgorithm.Loop,
        json, null, null, null, null);

    /// <summary>
    /// An AAPS snapshot as the decomposer stores it: <c>predBGs</c> split across the four curve
    /// columns, and the IOB curve copied into the default column.
    /// </summary>
    private static ApsPredictionPoint OrefFixture(long start, string iob, string zt, string? cob, string uam) => new(
        DateTimeOffset.FromUnixTimeMilliseconds(start).UtcDateTime, "openaps://AndroidAPS", true, AidAlgorithm.AndroidAps,
        iob, iob, zt, cob, uam);

    /// <summary>A curve of 25 values (two hours) at <paramref name="mgdl"/>.</summary>
    private static string Flat(double mgdl) => Json(Enumerable.Repeat(mgdl, 25));

    private static string Json(IEnumerable<double> values) =>
        "[" + string.Join(",", values.Select(v => v.ToString(System.Globalization.CultureInfo.InvariantCulture))) + "]";

    private static IEnumerable<GlucoseSample> Readings(long from, long to, double mgdl)
    {
        for (var t = from; t < to; t += 5 * Minute)
            yield return new GlucoseSample(t, mgdl);
    }

    private static PredictionAccuracyRange Calculate(
        IEnumerable<ApsPredictionPoint> points,
        IEnumerable<GlucoseSample> glucose,
        long from = 0,
        long to = 0,
        IEnumerable<long>? carbs = null,
        IEnumerable<long>? manualBoluses = null,
        IntervalSet? adjusted = null,
        long readingsUntil = long.MaxValue,
        IntervalSet? exclusions = null,
        LoopAnalysisMasks? filter = null,
        LoopAnalysisMaskSources? sources = null,
        long? historyVisibleFrom = null)
    {
        from = from == 0 ? From : from;
        to = to == 0 ? To : to;
        var inputs = new PredictionAccuracyInputs
        {
            Zone = Sydney,
            Snapshots = [.. points.Select(PredictionAccuracyCalculator.Parse).OfType<PredictionSnapshot>()],
            Glucose = [.. glucose.OrderBy(g => g.Mills)],
            CarbMills = [.. carbs ?? []],
            ManualBolusMills = [.. manualBoluses ?? []],
            Adjusted = adjusted ?? IntervalSet.Empty,
            ReadingsUntil = readingsUntil,
            GlucoseExclusions = exclusions ?? IntervalSet.Empty,
            HistoryVisibleFrom = historyVisibleFrom,
        };
        var mask = LoopAnalysisMask.Build(filter ?? new LoopAnalysisMasks(), from, to, Sydney, sources ?? new LoopAnalysisMaskSources());
        return PredictionAccuracyCalculator.Calculate(inputs, mask, from, to);
    }

    private static PredictionHorizonAccuracy At(PredictionCurveAccuracy curve, int minutes) =>
        curve.Horizons.Single(h => h.Minutes == minutes);

    [Fact]
    public void Loop_DefaultCurve_ErrorIsPredictedMinusTheReadingAtEachHorizon()
    {
        var start = Midnight + 10 * Hour;
        var rising = Json(Enumerable.Range(0, 25).Select(i => 100.0 + 2 * i));

        var range = Calculate([LoopFixture(start, rising)], Readings(start, start + 3 * Hour, 110));

        range.Predictions.Should().Be(1);
        range.Algorithms.Should().Equal(AidAlgorithm.Loop);
        var curve = range.Curves.Should().ContainSingle().Subject;
        curve.Curve.Should().Be(PredictionCurve.Default);
        curve.Contexts.Should().BeEquivalentTo(Enum.GetValues<PredictionContext>());
        At(curve, 30).Error!.Mean.Should().Be(2, "112 predicted at +30, 110 read");
        At(curve, 60).Error!.Mean.Should().Be(14);
        At(curve, 60).Mae.Should().Be(14);
        At(curve, 60).Mard.Should().Be(12.727);
        At(curve, 120).Error!.Mean.Should().Be(38);
        At(curve, 120).Error!.N.Should().Be(1);
        At(curve, 120).Error!.Sparse.Should().BeTrue();
        curve.Horizons.Select(h => h.SettingsSignal).Should().Equal(true, true, false, false);
    }

    [Fact]
    public void Oref_DefaultCurveIsTheIobCopy_AndIsNotReportedAgain()
    {
        var start = Midnight + 10 * Hour;

        var range = Calculate(
            [OrefFixture(start, Flat(120), Flat(100), Flat(150), Flat(130))],
            Readings(start, start + 3 * Hour, 110));

        range.Curves.Select(c => c.Curve).Should().Equal(
            PredictionCurve.Iob, PredictionCurve.ZeroTemp, PredictionCurve.Cob, PredictionCurve.Uam);
        range.Curves.Single(c => c.Curve == PredictionCurve.Iob).Horizons.Sum(h => h.Error?.N ?? 0)
            .Should().Be(4, "one pair per horizon, not two");
    }

    [Fact]
    public void Trio_WithoutADefaultCurve_ReadsTheOrefCurves()
    {
        var start = Midnight + 10 * Hour;
        var trio = new ApsPredictionPoint(
            DateTimeOffset.FromUnixTimeMilliseconds(start).UtcDateTime, "openaps://Trio", true, AidAlgorithm.Trio,
            null, Flat(120), Flat(100), null, Flat(130));

        var range = Calculate([trio], Readings(start, start + 3 * Hour, 110));

        range.Curves.Select(c => c.Curve).Should().Equal(PredictionCurve.Iob, PredictionCurve.ZeroTemp, PredictionCurve.Uam);
    }

    [Fact]
    public void Oref_EachCurveIsReportedOnlyInTheContextItIsReliedOnIn()
    {
        var fasting = Midnight + 4 * Hour;
        var meal = Midnight + 12 * Hour;
        var afterMeal = meal + 30 * Minute;

        var range = Calculate(
            [OrefFixture(fasting, Flat(120), Flat(100), Flat(150), Flat(130)),
             OrefFixture(afterMeal, Flat(140), Flat(90), Flat(160), Flat(170))],
            Readings(Midnight, To + 3 * Hour, 110),
            carbs: [meal]);

        var iob = range.Curves.Single(c => c.Curve == PredictionCurve.Iob);
        iob.Contexts.Should().Equal(PredictionContext.PostCorrection, PredictionContext.Fasting);
        At(iob, 30).Error!.Mean.Should().Be(10, "only the fasting prediction counts");
        iob.ByContext.Select(c => c.Context).Should().Equal(PredictionContext.PostCorrection, PredictionContext.Fasting);

        var cob = range.Curves.Single(c => c.Curve == PredictionCurve.Cob);
        cob.Contexts.Should().Equal(PredictionContext.PostMeal);
        At(cob, 30).Error!.Mean.Should().Be(50, "only the post-meal prediction counts");
        var uam = range.Curves.Single(c => c.Curve == PredictionCurve.Uam);
        uam.Contexts.Should().Equal(Enum.GetValues<PredictionContext>());
        At(uam, 30).Error!.Mean.Should().Be(40, "UAM counts with and without announced carbs");
        uam.ByContext.Single(c => c.Context == PredictionContext.Fasting).Horizons[0].Error!.Mean.Should().Be(20);
        uam.ByContext.Single(c => c.Context == PredictionContext.PostMeal).Horizons[0].Error!.Mean.Should().Be(60);
        At(range.Curves.Single(c => c.Curve == PredictionCurve.ZeroTemp), 30).Error!.Mean.Should().Be(-10);
    }

    [Fact]
    public void Context_TakesTheFirstThatApplies()
    {
        var at = Midnight + 12 * Hour;
        var inputs = new PredictionAccuracyInputs
        {
            Zone = Sydney,
            CarbMills = [at - 3 * Hour],
            ManualBolusMills = [at - Hour],
            Adjusted = IntervalSet.Of(at + Hour, at + 2 * Hour),
        };

        PredictionAccuracyCalculator.ContextAt(inputs, at).Should().Be(PredictionContext.PostMeal, "carbs exactly three hours back still count");
        PredictionAccuracyCalculator.ContextAt(inputs, at + 1).Should().Be(PredictionContext.PostCorrection);
        PredictionAccuracyCalculator.ContextAt(inputs, at + Hour).Should().Be(PredictionContext.Adjusted);
        PredictionAccuracyCalculator.ContextAt(inputs, at + 5 * Hour).Should().Be(PredictionContext.Fasting);
    }

    private static int[] Matched(PredictionCurveAccuracy curve, PredictionContext context) =>
        [.. curve.ByContext.Single(c => c.Context == context).Horizons.Select(h => h.Error?.N ?? 0)];

    [Fact]
    public void ACorrectionAfterAFastingPrediction_InterruptsTheHorizonsAfterIt()
    {
        var start = Midnight + 10 * Hour;

        var curve = Calculate(
            [LoopFixture(start, Flat(120))], Readings(Midnight, To + 3 * Hour, 110),
            manualBoluses: [start + 40 * Minute]).Curves.Single();

        Matched(curve, PredictionContext.Fasting).Should().Equal(1, 0, 0, 0);
        Matched(curve, PredictionContext.PostCorrection).Should().OnlyContain(n => n == 0, "the correction came after the prediction");
        curve.Horizons.Select(h => h.Interrupted).Should().Equal(0, 1, 1, 1);
        curve.ByHour[10].Horizons.Select(h => h.Error?.N ?? 0).Should().Equal(1, 0, 0, 0);
    }

    [Fact]
    public void AnAdjustmentStartedAfterThePrediction_InterruptsTheHorizonsAfterIt()
    {
        var start = Midnight + 10 * Hour;

        var curve = Calculate(
            [LoopFixture(start, Flat(120))], Readings(Midnight, To + 3 * Hour, 110),
            adjusted: IntervalSet.Of(start + 40 * Minute, start + 3 * Hour)).Curves.Single();

        Matched(curve, PredictionContext.Fasting).Should().Equal(1, 0, 0, 0);
        curve.Horizons.Select(h => h.Interrupted).Should().Equal(0, 1, 1, 1);
    }

    [Fact]
    public void AnAdjustmentAlreadyRunning_IsTheContext_NotAnInterruption()
    {
        var start = Midnight + 10 * Hour;

        var curve = Calculate(
            [LoopFixture(start, Flat(120))], Readings(Midnight, To + 3 * Hour, 110),
            adjusted: IntervalSet.Of(start - Hour, start + Hour)).Curves.Single();

        Matched(curve, PredictionContext.Adjusted).Should().Equal(1, 1, 1, 1);
        curve.Horizons.Select(h => h.Interrupted).Should().Equal(0, 0, 0, 0);
    }

    [Fact]
    public void ForAHistoryClampedCaller_APredictionWhoseContextReachesBehindTheEdge_IsNotCounted()
    {
        var edge = Midnight + 6 * Hour;
        var hidden = edge + PredictionAccuracyCalculator.ContextLookbackMs - 5 * Minute;
        var seen = edge + PredictionAccuracyCalculator.ContextLookbackMs;

        var range = Calculate(
            [LoopFixture(hidden, Flat(120)), LoopFixture(seen, Flat(120))], Readings(Midnight, To + 3 * Hour, 110),
            historyVisibleFrom: edge);

        range.Predictions.Should().Be(1, "a meal just before the edge would be hidden from the earlier one's context");
        range.Curves.Single().ByHour[9].Horizons[0].Error!.N.Should().Be(1);
    }

    [Fact]
    public void APreBolusStillActing_IsPostCorrection_UntilTheCarbsInterrupt()
    {
        var bolus = Midnight + 12 * Hour;
        var start = bolus + 5 * Minute;

        var curve = Calculate(
            [LoopFixture(start, Flat(120))], Readings(Midnight, To + 3 * Hour, 110),
            carbs: [bolus + 45 * Minute], manualBoluses: [bolus]).Curves.Single();

        Matched(curve, PredictionContext.PostCorrection).Should().Equal(1, 0, 0, 0);
        Matched(curve, PredictionContext.PostMeal).Should().OnlyContain(n => n == 0, "the carbs came after the prediction");
        curve.ByContext.Single(c => c.Context == PredictionContext.PostCorrection).Horizons
            .Select(h => h.Interrupted).Should().Equal(0, 1, 1, 1);
    }

    [Fact]
    public void APredictionMadeBeforeTheCarbsWereEntered_IsNotPostMeal()
    {
        var meal = Midnight + 12 * Hour;

        var curve = Calculate(
            [LoopFixture(meal - 45 * Minute, Flat(120))], Readings(Midnight, To + 3 * Hour, 110), carbs: [meal]).Curves.Single();

        Matched(curve, PredictionContext.Fasting).Should().Equal(1, 0, 0, 0);
        Matched(curve, PredictionContext.PostMeal).Should().Equal(0, 0, 0, 0);
        curve.Horizons.Select(h => h.Interrupted).Should().Equal(0, 1, 1, 1);
    }

    [Fact]
    public void AHorizonNotYetReached_IsNoPrediction()
    {
        var start = To - 10 * Minute;
        var now = To + 45 * Minute;

        var curve = Calculate(
            [LoopFixture(start, Flat(120))], Readings(To - Hour, now, 110), readingsUntil: now).Curves.Single();

        curve.Horizons.Select(h => h.Error?.N ?? 0).Should().Equal(1, 0, 0, 0);
        curve.Horizons.Should().OnlyContain(h => h.Unmatched == 0, "a reading that has not happened yet is not missing");
    }

    [Fact]
    public void APredictionStartedInADataExclusion_IsNotAnalysed()
    {
        var compressionLow = new MillsInterval(Midnight + 2 * Hour, Midnight + 3 * Hour);

        var range = Calculate(
            [LoopFixture(Midnight + 2 * Hour + 30 * Minute, Flat(40)), LoopFixture(Midnight + 5 * Hour, Flat(120))],
            Readings(Midnight, To, 110),
            sources: new LoopAnalysisMaskSources { DataExclusions = [compressionLow] });

        range.Predictions.Should().Be(1);
        At(range.Curves.Single(), 30).Error!.Mean.Should().Be(10);
    }

    [Fact]
    public void LoopsLaterValues_FallOnTheFiveMinuteGrid()
    {
        var start = Midnight + 10 * Hour + 2 * Minute + 30_000;
        var gridThirty = Midnight + 10 * Hour + 30 * Minute;
        var glucose = new[] { new GlucoseSample(gridThirty, 110), new GlucoseSample(start + 30 * Minute + 2 * Minute, 200) };

        var horizon = At(Calculate([LoopFixture(start, Flat(120))], glucose).Curves.Single(), 30);

        horizon.Error!.Mean.Should().Be(10, "value 6 is at the grid point 30 minutes after the start's, not 30 minutes after the start");
    }

    [Fact]
    public void TargetMills_PlacesLoopOnTheGridAndOrefFromTheStart()
    {
        var snapshot = new PredictionSnapshot(Midnight + 7 * Minute, AidAlgorithm.Loop, []);

        PredictionAccuracyCalculator.TargetMills(snapshot, PredictionCurve.Default, 0).Should().Be(Midnight + 7 * Minute);
        PredictionAccuracyCalculator.TargetMills(snapshot, PredictionCurve.Default, 6).Should().Be(Midnight + 35 * Minute);
        PredictionAccuracyCalculator.TargetMills(snapshot with { Algorithm = AidAlgorithm.Trio }, PredictionCurve.Iob, 6)
            .Should().Be(Midnight + 37 * Minute);
    }

    [Fact]
    public void Loop_IsBrokenDownByEveryContext()
    {
        var range = Calculate(
            [LoopFixture(Midnight + 2 * Hour, Flat(120)), LoopFixture(Midnight + 13 * Hour, Flat(150))],
            Readings(Midnight, To + 3 * Hour, 110),
            carbs: [Midnight + 12 * Hour]);

        var curve = range.Curves.Single();
        curve.ByContext.Select(c => c.Context).Should().Equal(Enum.GetValues<PredictionContext>());
        At(curve, 30).Error!.N.Should().Be(2);
        curve.ByContext.Single(c => c.Context == PredictionContext.PostMeal).Horizons[0].Error!.Mean.Should().Be(40);
        curve.ByContext.Single(c => c.Context == PredictionContext.Fasting).Horizons[0].Error!.Mean.Should().Be(10);
        curve.ByContext.Single(c => c.Context == PredictionContext.PostCorrection).Horizons[0].Error.Should().BeNull();
    }

    [Fact]
    public void ByHour_IsTheLocalHourThePredictionWasMadeIn()
    {
        var range = Calculate(
            [LoopFixture(Midnight + 7 * Hour + 55 * Minute, Flat(120))],
            Readings(Midnight, To, 110));

        var byHour = range.Curves.Single().ByHour;
        byHour.Should().HaveCount(24);
        byHour[7].Horizons[0].Error!.Mean.Should().Be(10);
        byHour[8].Horizons[0].Error.Should().BeNull("the +30 target is in hour 8, but the prediction was made in hour 7");
    }

    [Fact]
    public void APredictionRunningPastTheEndOfTheRange_IsPairedWithTheReadingsAfterIt()
    {
        var start = To - 10 * Minute;

        var range = Calculate([LoopFixture(start, Flat(120))], Readings(To - Hour, To + 3 * Hour, 110));

        var curve = range.Curves.Single();
        curve.Horizons.Should().OnlyContain(h => h.Error != null && h.Error.N == 1 && h.Unmatched == 0);
    }

    [Fact]
    public void LoopCycles_AreOnePerInstant_AcrossDeviceNames_AndApartWithinFiveMinutes()
    {
        var start = Midnight + 10 * Hour;
        ApsPredictionPoint Point(long at, string device, bool enacted) => new(
            DateTimeOffset.FromUnixTimeMilliseconds(at).UtcDateTime, device, enacted, AidAlgorithm.Loop,
            Flat(120), null, null, null, null);

        var distinct = LoopCycles.Distinct(
            [Point(start, "loop://iPhone", false), Point(start, "loop://Phone", true), Point(start + 3 * Minute, "loop://iPhone", false)],
            p => new DateTimeOffset(p.PredictedStart).ToUnixTimeMilliseconds(),
            p => p.Enacted);

        distinct.Select(p => (p.Device, p.Enacted)).Should().Equal(("loop://Phone", true), ("loop://iPhone", false));
    }

    [Fact]
    public void APredictionStartedOutsideTheRange_IsNotCounted()
    {
        var range = Calculate([LoopFixture(To, Flat(120)), LoopFixture(From - Minute, Flat(120))], Readings(From - Hour, To + 3 * Hour, 110));

        range.Predictions.Should().Be(0);
        range.Curves.Should().BeEmpty();
    }

    [Fact]
    public void AMissingReading_IsCountedUnmatched_NotImputed()
    {
        var start = Midnight + 10 * Hour;
        var glucose = Readings(start, start + 3 * Hour, 110).Where(g => g.Mills != start + 60 * Minute);

        var curve = Calculate([LoopFixture(start, Flat(120))], glucose).Curves.Single();

        At(curve, 60).Error.Should().BeNull();
        At(curve, 60).Mae.Should().BeNull();
        At(curve, 60).Unmatched.Should().Be(1);
        At(curve, 30).Unmatched.Should().Be(0);
        curve.ByHour[10].Horizons[1].Unmatched.Should().Be(1);
        curve.ByContext.Single(c => c.Context == PredictionContext.Fasting).Horizons[1].Unmatched.Should().Be(1);
    }

    [Theory]
    [InlineData(150_000, true)]
    [InlineData(-150_000, true)]
    [InlineData(150_001, false)]
    public void TheReadingMustBeWithinTwoAndAHalfMinutesOfTheTarget(long offsetMs, bool matched)
    {
        var start = Midnight + 10 * Hour;
        var target = start + 30 * Minute;

        var horizon = At(
            Calculate([LoopFixture(start, Flat(120))], [new GlucoseSample(target + offsetMs, 110)]).Curves.Single(), 30);

        (horizon.Error is not null).Should().Be(matched);
        horizon.Unmatched.Should().Be(matched ? 0 : 1);
    }

    [Fact]
    public void TheNearestReadingWins()
    {
        var start = Midnight + 10 * Hour;
        var target = start + 30 * Minute;

        var horizon = At(Calculate(
            [LoopFixture(start, Flat(120))],
            [new GlucoseSample(target - 2 * Minute, 90), new GlucoseSample(target + Minute, 110)]).Curves.Single(), 30);

        horizon.Error!.Mean.Should().Be(10);
    }

    [Fact]
    public void ACurveThatDoesNotReachAHorizon_MakesNoPredictionThere()
    {
        var start = Midnight + 10 * Hour;
        var oneHour = Json(Enumerable.Repeat(120.0, 13));

        var curve = Calculate([LoopFixture(start, oneHour)], Readings(start, start + 3 * Hour, 110)).Curves.Single();

        At(curve, 60).Error!.N.Should().Be(1);
        At(curve, 90).Error.Should().BeNull();
        At(curve, 90).Unmatched.Should().Be(0);
    }

    [Fact]
    public void AnOrefCurveCutShort_HoldsItsLastValue()
    {
        var start = Midnight + 10 * Hour;
        var levelled = Json(Enumerable.Range(0, 13).Select(i => 120.0 + i));

        var curve = Calculate(
            [OrefFixture(start, levelled, levelled, null, levelled)], Readings(start, start + 3 * Hour, 110)).Curves.First();

        At(curve, 60).Error!.Mean.Should().Be(22);
        At(curve, 120).Error!.Mean.Should().Be(22, "oref dropped the tail once the curve levelled off at 132");
    }

    [Fact]
    public void ACurveOrefCouldNotHaveCut_IsNotHeld()
    {
        var start = Midnight + 10 * Hour;
        var twelve = Json(Enumerable.Range(0, 12).Select(i => 120.0 + i));
        var zeroTempCut = Json(Enumerable.Range(0, 7).Select(i => 100.0 + i));

        var range = Calculate(
            [OrefFixture(start, twelve, zeroTempCut, null, twelve)], Readings(start, start + 3 * Hour, 110));

        At(range.Curves.Single(c => c.Curve == PredictionCurve.Iob), 60).Error
            .Should().BeNull("oref keeps at least 13 values, so a curve of 12 ended rather than levelled off");
        var zeroTemp = range.Curves.Single(c => c.Curve == PredictionCurve.ZeroTemp);
        At(zeroTemp, 30).Error!.N.Should().Be(1);
        At(zeroTemp, 60).Error.Should().BeNull("oref cuts the zero-temp curve where it turns up, not where it levels off");
    }

    [Fact]
    public void ANullValueInACurve_MakesNoPredictionThere()
    {
        var start = Midnight + 10 * Hour;
        var withGap = "[" + string.Join(",", Enumerable.Range(0, 25).Select(i => i == 6 ? "null" : "120")) + "]";

        var curve = Calculate(
            [OrefFixture(start, withGap, withGap, null, withGap)], Readings(start, start + 3 * Hour, 110)).Curves.First();

        At(curve, 30).Error.Should().BeNull();
        At(curve, 30).Unmatched.Should().Be(0);
        At(curve, 60).Error!.N.Should().Be(1);
    }

    [Fact]
    public void AMaskedStart_IsNotAnalysed()
    {
        var overrideSpan = new MillsInterval(Midnight + 9 * Hour, Midnight + 11 * Hour);

        var range = Calculate(
            [LoopFixture(Midnight + 10 * Hour, Flat(120)), LoopFixture(Midnight + 14 * Hour, Flat(130))],
            Readings(Midnight, To + 3 * Hour, 110),
            adjusted: IntervalSet.Of([overrideSpan]),
            filter: new LoopAnalysisMasks { ExcludeOverrides = true },
            sources: new LoopAnalysisMaskSources { Overrides = [overrideSpan] });

        range.Predictions.Should().Be(1);
        At(range.Curves.Single(), 30).Error!.Mean.Should().Be(20);
    }

    [Fact]
    public void AHorizonInADataExclusion_IsCountedExcluded_NeitherPairedNorUnmatched()
    {
        var start = Midnight + 10 * Hour;

        var curve = Calculate(
            [LoopFixture(start, Flat(120))],
            Readings(start, start + 3 * Hour, 110),
            exclusions: IntervalSet.Of(start + 55 * Minute, start + 65 * Minute)).Curves.Single();

        At(curve, 60).Error.Should().BeNull();
        At(curve, 60).Unmatched.Should().Be(0);
        At(curve, 60).Excluded.Should().Be(1);
        At(curve, 30).Excluded.Should().Be(0);
        At(curve, 30).Error!.N.Should().Be(1);
    }

    [Fact]
    public void Spread_IsOverTheSignedErrors()
    {
        var points = Enumerable.Range(0, 10)
            .Select(d => LoopFixture(Midnight - d * 24 * Hour + 10 * Hour, Flat(110 + d)));

        var horizon = At(
            Calculate(points, Readings(Midnight - 10 * 24 * Hour, To, 110), from: Midnight - 10 * 24 * Hour).Curves.Single(), 30);

        horizon.Error!.N.Should().Be(10);
        horizon.Error.P50.Should().Be(4.5);
        horizon.Error.P10.Should().Be(0.9);
        horizon.Error.Sparse.Should().BeFalse("ten days contributed");
    }

    [Fact]
    public void ACurveOfUnreadableJson_IsLeftOut()
    {
        PredictionAccuracyCalculator.Parse(LoopFixture(Midnight, "not json")).Should().BeNull();
        PredictionAccuracyCalculator.Parse(LoopFixture(Midnight, "[]")).Should().BeNull();
    }
}
