using Nocturne.Core.Clinical.LoopAnalysis;
using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Clinical.Treatments;
using Nocturne.Core.Models;
using Nocturne.Core.Models.LoopAnalysis;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Clinical.Tests.LoopAnalysis;

/// <summary>
/// The Meals and events cohorts a custom report adds: all others, the starts of lows and highs, and
/// the conditions on the state at the event.
/// </summary>
public class EventResponseCohortConditionTests
{
    private static readonly TimeZoneInfo Sydney = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");
    private const long Minute = 60_000;
    private const long Hour = 60 * Minute;

    private static long Local(int d, int h, int mi = 0) =>
        new DateTimeOffset(new DateTime(2024, 6, d, h, mi, 0), TimeSpan.FromHours(10)).ToUnixTimeMilliseconds();

    private static readonly long From = Local(10, 0);
    private static readonly long To = Local(17, 0);

    private static EventResponseRange Calculate(
        IEnumerable<EventCohortDefinition> cohorts,
        IEnumerable<GlucoseSample>? glucose = null,
        IEnumerable<CarbEvent>? carbs = null,
        IEnumerable<IobCobTick>? iobCob = null,
        LoopAnalysisMaskSources? sources = null,
        IReadOnlyDictionary<long, IReadOnlyList<MealFood>>? mealFoods = null,
        long? historyVisibleFrom = null,
        int minutesBefore = EventResponseDefinition.DefaultMinutesBefore)
    {
        var definition = new EventResponseDefinition { Cohorts = [.. cohorts], MinutesBefore = minutesBefore };
        var inputs = new EventResponseInputs
        {
            Series = new DailyProfileInputs
            {
                Zone = Sydney,
                Insulin = new NetInsulinAboveSchedule([], [], []),
                Glucose = [.. glucose ?? Flat(120)],
                Carbs = [.. carbs ?? []],
                IobCob = [.. iobCob ?? []],
            },
            Sources = sources ?? new LoopAnalysisMaskSources(),
            MealFoods = mealFoods ?? new Dictionary<long, IReadOnlyList<MealFood>>(),
            HistoryVisibleFrom = historyVisibleFrom,
        };
        var mask = LoopAnalysisMask.Build(new LoopAnalysisMasks(), From, To, Sydney, inputs.Sources);
        return EventResponseCalculator.Calculate(inputs, mask, From, To, definition);
    }

    private static IEnumerable<GlucoseSample> Readings(Func<long, double?> mgdl)
    {
        for (var t = From - 30 * Hour; t < To + 12 * Hour; t += 5 * Minute)
            if (mgdl(t) is { } value)
                yield return new GlucoseSample(t, value);
    }

    private static IEnumerable<GlucoseSample> Flat(double mgdl) => Readings(_ => mgdl);

    private static EventCohortDefinition Meal(int n) => new() { Cohort = n, Anchor = EventAnchorKind.Meal };

    private static EventCohortDefinition Exercise(int n) => new() { Cohort = n, Anchor = EventAnchorKind.ExerciseStart };

    private static LoopAnalysisMaskSources ExerciseAt(params long[] starts) =>
        new() { Exercise = [.. starts.Select(s => new MillsInterval(s, s + Hour))] };

    private static readonly Guid Pizza = Guid.NewGuid();
    private static readonly Guid Rice = Guid.NewGuid();

    [Fact]
    public void AllOthers_HoldsEveryMealThePizzaGroupDoesNotTakeIn_CarbsOnlyMealsIncluded()
    {
        var pizza = Local(11, 19);
        var rice = Local(12, 19);
        var carbsOnly = Local(13, 19);
        var pizzaAgain = Local(14, 19);
        CarbEvent[] carbs = [new(pizza, 80), new(rice, 50), new(carbsOnly, 30), new(pizzaAgain, 90)];
        var foods = new Dictionary<long, IReadOnlyList<MealFood>>
        {
            [pizza] = [new MealFood(Pizza, "Fast food")],
            [rice] = [new MealFood(Rice, "Grains")],
            [pizzaAgain] = [new MealFood(Pizza, "Fast food"), new MealFood(Rice, "Grains")],
        };

        var range = Calculate(
            [new EventCohortDefinition { Cohort = 1, Anchor = EventAnchorKind.Meal, FoodIds = [Pizza] }, new EventCohortDefinition { Cohort = 2, Anchor = EventAnchorKind.Meal, Others = true }],
            carbs: carbs, mealFoods: foods);
        var every = Calculate([Meal(1)], carbs: carbs, mealFoods: foods).Cohorts.Single();

        range.Cohorts[0].Events.Should().Be(2);
        range.Cohorts[1].Events.Should().Be(2, "the rice meal and the meal of carbs only");
        (range.Cohorts[0].Events + range.Cohorts[1].Events).Should().Be(every.Events);
        (range.Cohorts[0].Anchors + range.Cohorts[1].Anchors).Should().Be(every.Anchors);
        range.Cohorts[0].MealsWithoutFoodRecord.Should().Be(1, "the pizza group counts the carbs-only meal apart, and all others takes it in");
        range.Events.Should().OnlyContain(e => e.Cohorts.Count == 1, "no meal is in both groups");
        range.Events.Single(e => e.At == DateTimeOffset.FromUnixTimeMilliseconds(carbsOnly).UtcDateTime).Cohorts.Should().Equal(2);
    }

    [Fact]
    public void AllOthers_LeavesOutWhatAnyOverlappingGroupTakesIn()
    {
        var smallMorning = Local(11, 8);
        var largeMorning = Local(12, 8);
        var largeEvening = Local(13, 19);
        var smallEvening = Local(14, 19);
        CarbEvent[] carbs = [new(smallMorning, 20), new(largeMorning, 80), new(largeEvening, 70), new(smallEvening, 15)];

        var range = Calculate(
            [
                new EventCohortDefinition { Cohort = 1, Anchor = EventAnchorKind.Meal, FromMinute = 6 * 60, ToMinute = 10 * 60 },
                new EventCohortDefinition { Cohort = 2, Anchor = EventAnchorKind.Meal, MinGrams = 50 },
                new EventCohortDefinition { Cohort = 3, Anchor = EventAnchorKind.Meal, Others = true },
            ],
            carbs: carbs);

        range.Cohorts.Select(c => c.Events).Should().Equal(2, 2, 1);
        range.Events.Single(e => e.Cohorts.Contains(3)).At.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(smallEvening).UtcDateTime);
        range.Events.Single(e => e.At == DateTimeOffset.FromUnixTimeMilliseconds(largeMorning).UtcDateTime).Cohorts.Should().Equal(1, 2);
    }

    [Fact]
    public void AllOthers_IsTheRestOfItsOwnKindOnly()
    {
        var meal = Local(11, 8);
        var exercise = Local(12, 17);

        var range = Calculate(
            [Exercise(1), new EventCohortDefinition { Cohort = 2, Anchor = EventAnchorKind.Meal, Others = true }],
            carbs: [new(meal, 40)], sources: ExerciseAt(exercise));

        range.Cohorts[1].Events.Should().Be(1, "an exercise group takes in no meal");
    }

    [Fact]
    public void AllOthers_CountsNothing_WhenAGroupItIsTheRestOfHasItsFoodFilterWithheld()
    {
        var range = Calculate(
            [
                new EventCohortDefinition { Cohort = 1, Anchor = EventAnchorKind.Meal, FoodFilterWithheld = true },
                new EventCohortDefinition { Cohort = 2, Anchor = EventAnchorKind.Meal, Others = true },
            ],
            carbs: [new(Local(11, 8), 40)]);

        range.Cohorts[1].Events.Should().Be(0);
        range.Cohorts[1].Anchors.Should().Be(0);
    }

    [Fact]
    public void AllOthers_TakesInAnEventAGroupCouldNotReadTheIobOf()
    {
        var withIob = Local(11, 17);
        var withoutIob = Local(12, 17);

        var range = Calculate(
            [
                new EventCohortDefinition { Cohort = 1, Anchor = EventAnchorKind.ExerciseStart, MaxIob = 1 },
                new EventCohortDefinition { Cohort = 2, Anchor = EventAnchorKind.ExerciseStart, Others = true },
            ],
            sources: ExerciseAt(withIob, withoutIob),
            iobCob: [new(withIob - Minute, 0.4, IobCobSource.Device, 0, IobCobSource.Device)]);

        range.Cohorts[0].Events.Should().Be(1);
        range.Cohorts[0].MissingAnchorIob.Should().Be(1);
        range.Cohorts[1].Events.Should().Be(1);
    }

    /// <summary>A low of <paramref name="minutes"/> at 60 mg/dL from <paramref name="at"/>.</summary>
    private static Func<long, double?> Low(long at, long minutes, Func<long, double?> otherwise) =>
        t => t >= at && t < at + minutes * Minute ? 60 : otherwise(t);

    private static double? InRange(long _) => 120;

    [Fact]
    public void ALowStart_IsTheFirstReadingOfALowOfAtLeast15Minutes()
    {
        var low = Local(11, 3, 10);
        var dip = Local(12, 3, 10);

        var range = Calculate(
            [new EventCohortDefinition { Cohort = 1, Anchor = EventAnchorKind.LowStart }],
            glucose: Readings(Low(low, 30, Low(dip, 10, InRange))));

        range.Cohorts.Single().Events.Should().Be(1, "a 10-minute dip is no low");
        var e = range.Events.Single();
        e.At.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(low).UtcDateTime);
        e.Metrics.StartGlucose.Should().Be(60);
        e.Response.Should().Be(EventResponseKind.Fall);
    }

    [Fact]
    public void BackToBackLows_LessThan15MinutesApart_AreOneLow_AndAGapEndsOne()
    {
        var first = Local(11, 3);
        var resumed = first + 30 * Minute;
        var second = Local(12, 3);
        var afterReturn = second + 45 * Minute;
        var gapped = Local(13, 3);
        var afterGap = gapped + 50 * Minute;

        Func<long, double?> glucose = t =>
            t >= gapped + 20 * Minute && t < afterGap ? null
            : Low(first, 20, Low(resumed, 20, Low(second, 20, Low(afterReturn, 20, Low(gapped, 20, Low(afterGap, 20, InRange))))))(t);

        var range = Calculate([new EventCohortDefinition { Cohort = 1, Anchor = EventAnchorKind.LowStart }], glucose: Readings(glucose));

        range.Events.Select(e => e.At).Should().Equal(
            new[] { first, second, afterReturn, gapped, afterGap }.Select(m => DateTimeOffset.FromUnixTimeMilliseconds(m).UtcDateTime),
            "10 minutes back in range do not end a low, 25 minutes do, and so does a gap in the readings");
    }

    [Fact]
    public void LowAndHighStarts_AreTheEpisodesTheGlucoseStatisticsCount()
    {
        var readings = Readings(t =>
        {
            var minute = (t - From) / Minute % (24 * 60);
            return minute switch
            {
                >= 180 and < 215 => 62,
                >= 215 and < 225 => 75,
                >= 225 and < 260 => 50,
                >= 720 and < 900 => 230,
                >= 905 and < 915 => 170,
                >= 915 and < 960 => 260,
                _ => 120,
            };
        }).ToList();

        var range = Calculate(
            [new EventCohortDefinition { Cohort = 1, Anchor = EventAnchorKind.LowStart }, new EventCohortDefinition { Cohort = 2, Anchor = EventAnchorKind.HighStart }],
            glucose: readings);

        var episodes = GlucoseEpisodeDetector.Detect(
            [.. readings.Select(r => new SensorGlucose { Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(r.Mills).UtcDateTime, Mgdl = r.Mgdl })],
            new GlycemicThresholds());
        bool InRangeAndDone(GlucoseEpisode e) =>
            e.StartAt >= DateTimeOffset.FromUnixTimeMilliseconds(From).UtcDateTime && e.StartAt < DateTimeOffset.FromUnixTimeMilliseconds(To).UtcDateTime;

        range.Cohorts[0].Events.Should().Be(episodes.Count(e => e.BelowRange && InRangeAndDone(e))).And.Be(7);
        range.Cohorts[1].Events.Should().Be(episodes.Count(e => !e.BelowRange && InRangeAndDone(e))).And.Be(14,
            "15 minutes back in range end a high, so each day has two");
        range.Cohorts[1].Response.Should().Be(EventResponseKind.Rise);
    }

    [Fact]
    public void ALowStart_CanLookFourHoursBack()
    {
        var low = Local(11, 3);

        var range = Calculate(
            [new EventCohortDefinition { Cohort = 1, Anchor = EventAnchorKind.LowStart }],
            glucose: Readings(Low(low, 30, InRange)), minutesBefore: 240);

        range.Cohorts.Single().Slots.Should().HaveCount((240 + EventResponseDefinition.DefaultMinutesAfter) / 5);
        range.Cohorts.Single().Slots[0].Glucose!.P50.Should().Be(120);
    }

    [Theory]
    [InlineData(240, null, 240, true)]
    [InlineData(240, null, 239, false)]
    [InlineData(null, 60, 60, true)]
    [InlineData(null, 60, 61, false)]
    [InlineData(60, 120, 90, true)]
    [InlineData(60, 120, 121, false)]
    public void MinutesSinceMeal_BoundsAreInclusive(int? min, int? max, int carbsMinutesBefore, bool counted)
    {
        var exercise = Local(11, 17);

        var range = Calculate(
            [new EventCohortDefinition { Cohort = 1, Anchor = EventAnchorKind.ExerciseStart, MinMinutesSinceMeal = min, MaxMinutesSinceMeal = max }],
            carbs: [new(exercise - carbsMinutesBefore * Minute, 30)],
            sources: ExerciseAt(exercise));

        range.Cohorts.Single().Events.Should().Be(counted ? 1 : 0);
    }

    [Fact]
    public void MinutesSinceMeal_WithNoCarbsAtAll_MeetsALowestAndNeverAHighest()
    {
        var exercise = Local(11, 17);

        var noCarbs = Calculate(
            [
                new EventCohortDefinition { Cohort = 1, Anchor = EventAnchorKind.ExerciseStart, MinMinutesSinceMeal = 240 },
                new EventCohortDefinition { Cohort = 2, Anchor = EventAnchorKind.ExerciseStart, MaxMinutesSinceMeal = 1440 },
            ],
            sources: ExerciseAt(exercise));

        noCarbs.Cohorts.Select(c => c.Events).Should().Equal(1, 0);
    }

    [Fact]
    public void MinutesSinceMeal_ForAMeal_IsFromTheMealBeforeIt()
    {
        var lunch = Local(11, 12);
        var dinner = Local(11, 19);

        var range = Calculate(
            [new EventCohortDefinition { Cohort = 1, Anchor = EventAnchorKind.Meal, MinMinutesSinceMeal = 300 }],
            carbs: [new(lunch, 50), new(dinner, 60)]);

        range.Events.Should().HaveCount(2, "lunch has no carbs in the day before it, and dinner is 7 hours after lunch");
    }

    [Fact]
    public void AMinutesSinceMealCondition_ReadsOnlyHistoryTheReaderSees()
    {
        var visibleFrom = Local(11, 0);
        var early = visibleFrom + 5 * Hour;
        var late = visibleFrom + 10 * Hour;

        var range = Calculate(
            [new EventCohortDefinition { Cohort = 1, Anchor = EventAnchorKind.ExerciseStart, MinMinutesSinceMeal = 480 }],
            sources: ExerciseAt(early, late), historyVisibleFrom: visibleFrom);

        var cohort = range.Cohorts.Single();
        cohort.BeforeVisibleHistory.Should().Be(1, "8 hours before the early start reach past what the reader sees");
        cohort.Events.Should().Be(1);
        EventResponseCalculator.HistoryLookbackMs(EventWindow.Of(new EventResponseDefinition()),
                new EventResponseDefinition { Cohorts = [new EventCohortDefinition { MinMinutesSinceMeal = 480 }] })
            .Should().Be(8 * Hour);
    }

    [Theory]
    [InlineData(70.0, null, true)]
    [InlineData(71.0, null, false)]
    [InlineData(null, 70.0, true)]
    [InlineData(null, 69.0, false)]
    public void StartGlucose_BoundsAreInclusive(double? min, double? max, bool counted)
    {
        var exercise = Local(11, 17);

        var range = Calculate(
            [new EventCohortDefinition { Cohort = 1, Anchor = EventAnchorKind.ExerciseStart, MinStartGlucose = min, MaxStartGlucose = max }],
            glucose: Flat(70), sources: ExerciseAt(exercise));

        range.Cohorts.Single().Events.Should().Be(counted ? 1 : 0);
    }

    [Fact]
    public void AStartGlucoseCondition_OnAnEventWithNoReading_IsCountedAsWithoutAReading()
    {
        var exercise = Local(11, 17);

        var range = Calculate(
            [new EventCohortDefinition { Cohort = 1, Anchor = EventAnchorKind.ExerciseStart, MinStartGlucose = 100 }],
            glucose: Readings(t => Math.Abs(t - exercise) <= 10 * Minute ? null : 120), sources: ExerciseAt(exercise));

        range.Cohorts.Single().MissingAnchorGlucose.Should().Be(1);
    }

    [Theory]
    [InlineData(1.5, null, true)]
    [InlineData(1.6, null, false)]
    [InlineData(null, 1.5, true)]
    [InlineData(null, 1.4, false)]
    [InlineData(-1.0, 0.0, false)]
    public void Iob_BoundsAreInclusive(double? min, double? max, bool counted)
    {
        var exercise = Local(11, 17);

        var range = Calculate(
            [new EventCohortDefinition { Cohort = 1, Anchor = EventAnchorKind.ExerciseStart, MinIob = min, MaxIob = max }],
            sources: ExerciseAt(exercise),
            iobCob: [new(exercise - 2 * Minute, 1.5, IobCobSource.Device, 0, IobCobSource.Device)]);

        range.Cohorts.Single().Events.Should().Be(counted ? 1 : 0);
        range.Cohorts.Single().MissingAnchorIob.Should().Be(0);
    }
}
