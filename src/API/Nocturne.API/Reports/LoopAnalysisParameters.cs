using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Nocturne.API.Controllers.V4.Analytics;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.LoopAnalysis;

namespace Nocturne.API.Reports;

/// <summary>
/// The masks every Loop Analysis dataset takes, as <see cref="LoopAnalysisQuery"/> names them. A
/// mask the token lacks the scope for is dropped and named in the panel summary.
/// </summary>
internal abstract record LoopAnalysisDatasetParameters : ReportDatasetParameters
{
    public const int MaxProfileNameLength = 100;

    [Description("Leave out time an override ran.")]
    [ReportNeedsScope(Scope.TreatmentsRead)]
    public bool ExcludeOverrides { get; init; }

    [Description("Leave out time a temporary target ran.")]
    [ReportNeedsScope(Scope.TreatmentsRead)]
    public bool ExcludeTemporaryTargets { get; init; }

    [Description("Leave out exercise time.")]
    [ReportNeedsScope(Scope.TreatmentsRead)]
    public bool ExcludeExercise { get; init; }

    [Description("Leave out illness time.")]
    [ReportNeedsScope(Scope.TreatmentsRead)]
    public bool ExcludeIllness { get; init; } = true;

    [Description("Leave out time the pump was in manual mode or had no AID algorithm.")]
    [ReportNeedsScope(Scope.DevicesRead)]
    [DisplayName("Exclude manual mode")]
    public bool ExcludeOpenLoop { get; init; } = true;

    [Description("Analyze only time this profile was active. Optional.")]
    [ReportNeedsScope(Scope.TherapyRead)]
    [StringLength(MaxProfileNameLength)]
    public string? ProfileName { get; init; }

    /// <summary>The local weekdays analysed, or null for every day.</summary>
    [JsonIgnore]
    public virtual IReadOnlyList<ReportWeekday>? Weekdays => null;

    /// <summary>The query for local dates <paramref name="start"/> to <paramref name="end"/>, with these masks.</summary>
    public LoopAnalysisQuery ToQuery(DateOnly start, DateOnly end) => new()
    {
        StartDate = start,
        EndDate = end,
        Days = Weekdays is { Count: > 0 } days ? [.. days.Select(d => d.ToDayOfWeek())] : null,
        ExcludeOverrides = ExcludeOverrides,
        ExcludeTemporaryTargets = ExcludeTemporaryTargets,
        ExcludeExercise = ExcludeExercise,
        ExcludeIllness = ExcludeIllness,
        ExcludeOpenLoop = ExcludeOpenLoop,
        ProfileName = string.IsNullOrWhiteSpace(ProfileName) ? null : ProfileName.Trim(),
    };

    /// <summary>
    /// Every mask that is on, by its query parameter, and the profile name when given: the ones whose
    /// drop for want of a scope the summary reports, the defaults included, since a dropped default
    /// changes what was analysed as much as a dropped request does.
    /// </summary>
    public IReadOnlySet<string> Sent()
    {
        var sent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (ExcludeOverrides) sent.Add("excludeOverrides");
        if (ExcludeTemporaryTargets) sent.Add("excludeTemporaryTargets");
        if (ExcludeExercise) sent.Add("excludeExercise");
        if (ExcludeIllness) sent.Add("excludeIllness");
        if (ExcludeOpenLoop) sent.Add("excludeOpenLoop");
        if (!string.IsNullOrWhiteSpace(ProfileName)) sent.Add("profileName");
        return sent;
    }
}

/// <summary>A local weekday, as a cohort filter names it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReportWeekday>))]
internal enum ReportWeekday
{
    [JsonStringEnumMemberName("Mon")] Mon,
    [JsonStringEnumMemberName("Tue")] Tue,
    [JsonStringEnumMemberName("Wed")] Wed,
    [JsonStringEnumMemberName("Thu")] Thu,
    [JsonStringEnumMemberName("Fri")] Fri,
    [JsonStringEnumMemberName("Sat")] Sat,
    [JsonStringEnumMemberName("Sun")] Sun,
}

internal static class ReportWeekdays
{
    public static DayOfWeek ToDayOfWeek(this ReportWeekday day) => day switch
    {
        ReportWeekday.Mon => DayOfWeek.Monday,
        ReportWeekday.Tue => DayOfWeek.Tuesday,
        ReportWeekday.Wed => DayOfWeek.Wednesday,
        ReportWeekday.Thu => DayOfWeek.Thursday,
        ReportWeekday.Fri => DayOfWeek.Friday,
        ReportWeekday.Sat => DayOfWeek.Saturday,
        ReportWeekday.Sun => DayOfWeek.Sunday,
        _ => throw new ArgumentOutOfRangeException(nameof(day), day, null),
    };
}

/// <summary>What a local day had, as a day filter names it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReportDayKind>))]
internal enum ReportDayKind
{
    [JsonStringEnumMemberName("exercise")] Exercise,
    [JsonStringEnumMemberName("illness")] Illness,
    [JsonStringEnumMemberName("low")] [Description("A low")] Low,
    [JsonStringEnumMemberName("lowOvernight")] [Description("A low between 00:00 and 06:00")] LowOvernight,
    [JsonStringEnumMemberName("carbs")] [Description("Carbs in a range")] Carbs,
}

[Description(
    "Keep only the local days that had something, or with 'others' every other day, so two layers can set them side by side. "
    + "A day the token cannot read whole is in neither group.")]
[DisplayName("Days")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ReportDayFilter
{
    public const double CarbsLimit = 2000;

    [Description(
        "What the day had: exercise (an exercise span on the day), illness (an illness span on the day), low (a low on the day, "
        + "as the glucose statistics count lows: at least 15 minutes under 70 mg/dL), lowOvernight (such a low between 00:00 and "
        + "06:00 local time), or carbs (the day's carbs in the range of minCarbs and maxCarbs). Exercise, illness and carbs need the "
        + "treatments permission.")]
    [DisplayName("The day had")]
    [JsonRequired]
    public ReportDayKind Kind { get; init; }

    [Description("Carbs only: least carbs in the day, g, 0 to 2000. Optional.")]
    [Range(0, CarbsLimit)]
    public double? MinCarbs { get; init; }

    [Description("Carbs only: most carbs in the day, g, 0 to 2000. Optional.")]
    [Range(0, CarbsLimit)]
    public double? MaxCarbs { get; init; }

    [Description("Keep every other day: the days that did not have it.")]
    [DisplayName("All other days")]
    public bool Others { get; init; }

    [JsonIgnore]
    public bool NeedsTreatments => Kind is ReportDayKind.Exercise or ReportDayKind.Illness or ReportDayKind.Carbs;

    public DayCondition ToCondition() => new()
    {
        Kind = Kind switch
        {
            ReportDayKind.Exercise => DayConditionKind.Exercise,
            ReportDayKind.Illness => DayConditionKind.Illness,
            ReportDayKind.Low => DayConditionKind.Low,
            ReportDayKind.LowOvernight => DayConditionKind.LowOvernight,
            ReportDayKind.Carbs => DayConditionKind.Carbs,
            _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, null),
        },
        MinCarbs = MinCarbs,
        MaxCarbs = MaxCarbs,
        Others = Others,
    };

    /// <summary>The days kept, in words: "days with exercise", "days without a low", "days with less than 150 g of carbs".</summary>
    public string Words()
    {
        static string G(double grams) => grams.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        if (Kind == ReportDayKind.Carbs)
            return (MinCarbs, MaxCarbs, Others) switch
            {
                ({ } low, { } high, false) => $"days with {G(low)} to {G(high)} g of carbs",
                ({ } low, { } high, true) => $"days with less than {G(low)} or more than {G(high)} g of carbs",
                ({ } low, null, false) => $"days with {G(low)} g of carbs or more",
                ({ } low, null, true) => $"days with less than {G(low)} g of carbs",
                (null, { } high, false) => $"days with {G(high)} g of carbs or less",
                (null, { } high, true) => $"days with more than {G(high)} g of carbs",
                _ => "every day",
            };
        var what = Kind switch
        {
            ReportDayKind.Exercise => "exercise",
            ReportDayKind.Illness => "illness",
            ReportDayKind.Low => "a low",
            _ => "a low between 00:00 and 06:00",
        };
        return Others ? $"days without {what}" : $"days with {what}";
    }

    /// <summary>
    /// How the days fell, as a summary line: "Only days with exercise: 6 of 28 days; 22 did not have it. 1 day the
    /// token cannot read whole is in neither group."
    /// </summary>
    public string Summary(DaySelection? selection)
    {
        if (selection is null)
            return $"Only {Words()}.";
        var decided = selection.Matching + selection.NotMatching;
        var line = $"Only {Words()}: {selection.Kept.Count} of {(decided == 1 ? "1 day" : $"{decided} days")}.";
        if (selection.Undecided > 0)
            line += $" {(selection.Undecided == 1 ? "1 day starts" : $"{selection.Undecided} days start")} before the history this token reads, "
                + "so it is in neither group.";
        return line;
    }

    public IEnumerable<(string Field, string Message)> Validate(string at)
    {
        if (!Enum.IsDefined(Kind))
            yield return ($"{at}.kind", "Send one of: exercise, illness, low, lowOvernight, carbs.");
        var carbs = Kind == ReportDayKind.Carbs;
        if (!carbs && (MinCarbs ?? MaxCarbs) is not null)
            yield return ($"{at}.minCarbs", "Only a filter of carbs has carb limits. Remove them, or send kind carbs.");
        if (carbs && MinCarbs is null && MaxCarbs is null)
            yield return ($"{at}.minCarbs", "Send minCarbs, maxCarbs, or both.");
        if (MinCarbs is < 0 or > CarbsLimit || MaxCarbs is < 0 or > CarbsLimit)
            yield return ($"{at}.minCarbs", $"Send grams from 0 to {CarbsLimit}.");
        else if (MinCarbs > MaxCarbs)
            yield return ($"{at}.minCarbs", "Send a lowest that is not above the highest.");
    }
}

[Description("A local time-of-day window, minutes after midnight. With 'toMinute' before 'fromMinute' it wraps past midnight.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ReportTimeOfDay
{
    [Description("Start, minutes after local midnight, 0 to 1439.")]
    [DisplayName("From")]
    [Range(0, 1439)]
    [ReportInput(ReportInputAttribute.MinuteOfDay)]
    [JsonRequired]
    public int FromMinute { get; init; }

    [Description("End, exclusive, minutes after local midnight, 0 to 1439.")]
    [DisplayName("To")]
    [Range(0, 1439)]
    [ReportInput(ReportInputAttribute.MinuteOfDay)]
    [JsonRequired]
    public int ToMinute { get; init; }
}

[Description("A meal size range in grams of carbs, inclusive. Meal cohorts only.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ReportGrams
{
    [Description("Smallest meal, g, 0 to 1000. Optional.")]
    [Range(0, EventResponseDefinition.MaxGrams)]
    public double? Min { get; init; }

    [Description("Largest meal, g, 0 to 1000. Optional.")]
    [Range(0, EventResponseDefinition.MaxGrams)]
    public double? Max { get; init; }
}

[Description(
    "Minutes from the last carb entry before the event to the event, inclusive. "
    + "With only the lowest, 240 says no carbs in the 4 hours before the event.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ReportMinutesSinceMeal
{
    [Description("Fewest minutes since the last carbs, 1 to 1440. Optional.")]
    [Range(1, EventResponseDefinition.MaxMinutesSinceMeal)]
    public int? Min { get; init; }

    [Description("Most minutes since the last carbs, 1 to 1440. Optional.")]
    [Range(1, EventResponseDefinition.MaxMinutesSinceMeal)]
    public int? Max { get; init; }

    [JsonIgnore]
    public bool IsSet => Min is not null || Max is not null;
}

[Description(
    "The glucose at the event, the start glucose of its figures, inclusive. Always in mg/dL, "
    + "whatever unit the reports show: 70 mg/dL is 3.9 mmol/L, and 180 mg/dL is 10.0 mmol/L.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ReportStartGlucose
{
    /// <summary>The highest bound a condition may name, mg/dL: the highest plausible reading.</summary>
    public const int MaxMgdl = (int)Core.Constants.GlucoseConstants.MaxPlausibleMgdl;

    [Description("Lowest glucose at the event, mg/dL, 0 to 600. Optional.")]
    [Range(0, MaxMgdl)]
    public int? Min { get; init; }

    [Description("Highest glucose at the event, mg/dL, 0 to 600. Optional.")]
    [Range(0, MaxMgdl)]
    public int? Max { get; init; }

    [JsonIgnore]
    public bool IsSet => Min is not null || Max is not null;
}

[Description("The insulin on board at the event, U, inclusive. It can be below 0 when less insulin than the schedule was given.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ReportInsulinOnBoard
{
    public const double Lowest = -20;
    public const double Highest = 100;

    [Description("Least IOB, U, -20 to 100. Optional.")]
    [Range(Lowest, Highest)]
    public double? Min { get; init; }

    [Description("Most IOB, U, -20 to 100. Optional.")]
    [Range(Lowest, Highest)]
    public double? Max { get; init; }

    [JsonIgnore]
    public bool IsSet => Min is not null || Max is not null;
}

[Description(
    "One cohort of events: an anchor kind, narrowed by time of day, weekday and, for meals, by size and by the foods "
    + "the meal carries a food record of, and by the state at the event: time since carbs, glucose and IOB. "
    + "Or all the events of its kind that the other groups do not take in.")]
[DisplayName("Event group")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ReportEventCohort
{
    [Description(
        "What the events are aligned on: Meal (a carb entry), CorrectionBolus (a manual bolus with no meal near it), "
        + "SensorStart, ExerciseStart, OverrideStart, TemporaryTargetStart, UnannouncedRise (a meal-like rise with no carbs logged), "
        + "LowStart, HighStart, SiteChange (a site, cannula or pod change), SleepStart and Wake (the start and end of a sleep "
        + "session, naps included; need the sleep permission), LowTreatment (the first carbs logged during a low) or AnyBolus "
        + "(every manual bolus, for a meal or not). LowStart is the first reading of a low and HighStart the first reading of a high, by the rule "
        + "the glucose statistics count lows and highs by: a low starts after at least 15 minutes of readings below 70 mg/dL "
        + "(3.9 mmol/L), a high after at least 15 minutes above 180 mg/dL (10.0 mmol/L), and each ends after at least "
        + "15 minutes back in range or at a gap in the readings. These limits are the same for everyone, not the profile's target.")]
    [DisplayName("Event")]
    [JsonRequired]
    public EventAnchorKind Anchor { get; init; }

    [Description(
        "All the events of this kind in the range that no other group of the same kind takes in. "
        + "It takes no filter or condition of its own, and needs another group of the same kind. One group at most.")]
    [DisplayName("All others")]
    public bool Others { get; init; }

    [Description("Only events in this local time-of-day window. Optional.")]
    public ReportTimeOfDay? TimeOfDay { get; init; }

    [Description("Meal cohorts only: only meals of this size. Optional.")]
    [DisplayName("Meal size (g)")]
    public ReportGrams? Grams { get; init; }

    [Description("Only events on these local weekdays. Optional; every day when missing.")]
    [DisplayName("Weekdays")]
    [Length(1, 7)]
    public IReadOnlyList<ReportWeekday>? DayOfWeek { get; init; }

    [Description(
        "Meal groups only: the foods to match. A meal matches when it has a food record of one of them. "
        + "Meals with carbs only and no food record cannot match and are counted apart.")]
    [ReportNeedsScope(Scope.FoodRead)]
    [Length(1, EventResponseDefinition.MaxFoods)]
    [ReportInput(ReportInputAttribute.Foods)]
    public IReadOnlyList<Guid>? Foods { get; init; }

    [Description("Meal groups only: a food category. A meal matches when it has a food record of that category.")]
    [ReportNeedsScope(Scope.FoodRead)]
    [StringLength(EventResponseDefinition.MaxFoodCategoryLength)]
    [ReportInput(ReportInputAttribute.FoodCategory)]
    public string? FoodCategory { get; init; }

    [Description("Only events with this many minutes since the last carb entry before them. Optional.")]
    [DisplayName("Minutes since carbs")]
    [ReportNeedsScope(Scope.TreatmentsRead)]
    public ReportMinutesSinceMeal? MinutesSinceMeal { get; init; }

    [Description("Only events with a glucose at the event in this range, in mg/dL. Optional.")]
    [DisplayName("Glucose at the event")]
    public ReportStartGlucose? StartGlucose { get; init; }

    [Description("Only events with this insulin on board at the event. Events with no IOB value at the time are counted apart. Optional.")]
    [DisplayName("IOB at the event")]
    [ReportNeedsScope(Scope.TreatmentsRead)]
    public ReportInsulinOnBoard? InsulinOnBoard { get; init; }

    [JsonIgnore]
    public bool HasFoodFilter => Foods is { Count: > 0 } || !string.IsNullOrWhiteSpace(FoodCategory);

    /// <summary>A condition that reads carbs or IOB, which come from treatments.</summary>
    [JsonIgnore]
    public bool ReadsTreatments => MinutesSinceMeal is { IsSet: true } || InsulinOnBoard is { IsSet: true };

    /// <summary>Any filter or condition, which an <see cref="Others"/> group may not carry.</summary>
    [JsonIgnore]
    public bool IsNarrowed =>
        TimeOfDay is not null || Grams is not null || DayOfWeek is not null || Foods is not null || FoodCategory is not null
        || MinutesSinceMeal is not null || StartGlucose is not null || InsulinOnBoard is not null;
}

[Description("A comparison range of local calendar dates, inclusive.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ReportDateRange
{
    [Description("First local date, yyyy-MM-dd.")]
    [JsonRequired]
    public DateOnly From { get; init; }

    [Description("Last local date, inclusive, yyyy-MM-dd.")]
    [JsonRequired]
    public DateOnly To { get; init; }
}

[Description(
    "event_response: glucose, glucose change, IOB, COB and net loop correction aligned on up to six cohorts of events "
    + "(meals, correction boluses, sensor starts, exercise, overrides, temporary targets, unannounced rises, the starts of "
    + "lows and highs), each spread across its events as percentiles in five-minute slots from the event; and each event "
    + "with its figures.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal record EventResponseParameters : LoopAnalysisDatasetParameters
{
    public const string Name = "event_response";

    [JsonIgnore]
    public override string DatasetName => Name;

    /// <summary>
    /// The most groups a report asks for. The Meals and events endpoint takes three
    /// (<see cref="EventResponseDefinition.MaxCohorts"/>, its numbered query); a report builds each
    /// group by that query alone (<see cref="ToDefinition"/>), so it is held only by the cost of a read.
    /// </summary>
    public const int MaxGroups = 6;

    [Description("The cohorts, 1 to 6. Optional; every meal when missing. The Meals and events page shows at most 3.")]
    [Length(1, MaxGroups)]
    [DisplayName("Event groups")]
    public IReadOnlyList<ReportEventCohort>? Cohorts { get; init; }

    [Description("The part of each event's window before the event.")]
    [Range(0, 240)]
    [AllowedValues(0, 30, 60, 90, 120, 180, 240)]
    [DisplayName("Minutes before each event")]
    public int MinutesBefore { get; init; } = EventResponseDefinition.DefaultMinutesBefore;

    [Description("The part of each event's window after the event.")]
    [Range(120, 480)]
    [AllowedValues(120, 180, 240, 300, 360, 420, 480)]
    [DisplayName("Minutes after each event")]
    public int MinutesAfter { get; init; } = EventResponseDefinition.DefaultMinutesAfter;

    [Description("Only events with no other meal, correction bolus or unannounced rise in their window.")]
    [ReportNeedsScope(Scope.TreatmentsRead)]
    [DisplayName("Only events with nothing else near them")]
    public bool IsolatedOnly { get; init; }

    [Description("Count an unannounced rise only where the loop answered it with extra insulin.")]
    [ReportNeedsScope(Scope.TreatmentsRead)]
    [DisplayName("Count only rises with extra insulin")]
    public bool RequireCorroboration { get; init; }

    [Description("A second range of local dates to compare with, drawn as its own series. Optional.")]
    public ReportDateRange? ComparisonRange { get; init; }

    /// <summary>The cohorts asked for, or every meal.</summary>
    [JsonIgnore]
    public IReadOnlyList<ReportEventCohort> AppliedCohorts =>
        Cohorts is { Count: > 0 } cohorts ? cohorts : [new ReportEventCohort { Anchor = EventAnchorKind.Meal }];

    /// <summary>A group the Meals and events page cannot show: all others, or a condition on the state at the event.</summary>
    [JsonIgnore]
    public bool HasReportOnlyCohorts => AppliedCohorts.Count > EventResponseDefinition.MaxCohorts || AppliedCohorts.Any(c =>
        c.Others || c.MinutesSinceMeal is { IsSet: true } || c.StartGlucose is { IsSet: true } || c.InsulinOnBoard is { IsSet: true });

    /// <summary>
    /// The definition of these cohorts: the endpoint's (<see cref="ToEventQuery"/>) with what its
    /// query cannot carry, the all-others group and the conditions, set on each cohort.
    /// </summary>
    public EventResponseDefinition ToDefinition(IReadOnlyList<ReportEventCohort> cohorts)
    {
        var definition = ToEventQuery(cohorts.Take(1).ToList()).ToDefinition();
        definition.Cohorts =
        [
            .. cohorts.Select((asked, i) =>
            {
                var cohort = ToEventQuery([asked]).ToDefinition().Cohorts.Single();
                cohort.Cohort = i + 1;
                return cohort;
            }),
        ];
        foreach (var cohort in definition.Cohorts)
        {
            var asked = cohorts[cohort.Cohort - 1];
            cohort.Others = asked.Others;
            cohort.MinMinutesSinceMeal = asked.MinutesSinceMeal?.Min;
            cohort.MaxMinutesSinceMeal = asked.MinutesSinceMeal?.Max;
            cohort.MinStartGlucose = asked.StartGlucose?.Min;
            cohort.MaxStartGlucose = asked.StartGlucose?.Max;
            cohort.MinIob = asked.InsulinOnBoard?.Min;
            cohort.MaxIob = asked.InsulinOnBoard?.Max;
        }
        return definition;
    }

    /// <summary>
    /// The REST endpoint's query for these cohorts, numbered in order, so the definition is the one
    /// <see cref="EventResponseQuery.ToDefinition"/> makes of it.
    /// </summary>
    public EventResponseQuery ToEventQuery(IReadOnlyList<ReportEventCohort> cohorts)
    {
        var query = new EventResponseQuery
        {
            MinutesBefore = MinutesBefore,
            MinutesAfter = MinutesAfter,
            IsolatedOnly = IsolatedOnly,
            RequireCorroboration = RequireCorroboration,
        };
        for (var i = 0; i < cohorts.Count; i++)
        {
            var c = cohorts[i];
            var days = c.DayOfWeek is { Count: > 0 } d ? d.Select(w => w.ToDayOfWeek()).ToArray() : null;
            var foods = c.Foods is { Count: > 0 } f ? f.ToArray() : null;
            switch (i)
            {
                case 0:
                    query.Anchor1 = c.Anchor; query.FromMinute1 = c.TimeOfDay?.FromMinute; query.ToMinute1 = c.TimeOfDay?.ToMinute;
                    query.MinGrams1 = c.Grams?.Min; query.MaxGrams1 = c.Grams?.Max;
                    query.FoodIds1 = foods; query.FoodCategory1 = c.FoodCategory; query.DayOfWeek1 = days;
                    break;
                case 1:
                    query.Anchor2 = c.Anchor; query.FromMinute2 = c.TimeOfDay?.FromMinute; query.ToMinute2 = c.TimeOfDay?.ToMinute;
                    query.MinGrams2 = c.Grams?.Min; query.MaxGrams2 = c.Grams?.Max;
                    query.FoodIds2 = foods; query.FoodCategory2 = c.FoodCategory; query.DayOfWeek2 = days;
                    break;
                case 2:
                    query.Anchor3 = c.Anchor; query.FromMinute3 = c.TimeOfDay?.FromMinute; query.ToMinute3 = c.TimeOfDay?.ToMinute;
                    query.MinGrams3 = c.Grams?.Min; query.MaxGrams3 = c.Grams?.Max;
                    query.FoodIds3 = foods; query.FoodCategory3 = c.FoodCategory; query.DayOfWeek3 = days;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(cohorts), "At most three cohorts.");
            }
        }
        return query;
    }
}

/// <summary>
/// The event groups of <see cref="EventResponseParameters"/> read event by event, for the datasets that
/// draw each event: the primary range only, since the comparison range's events are not listed.
/// </summary>
internal abstract record EventListParameters : EventResponseParameters
{
    [Description(
        "Only the events of this group, by its number in the list of groups. Optional; the events of every group when missing. "
        + "To set two groups side by side, draw a layer for each.")]
    [DisplayName("Group")]
    [Range(1, MaxGroups)]
    public int? Group { get; init; }
}

[Description(
    "event_outcomes: each event of up to six groups as one row: its figures (peak rise, drop, lowest reading, minutes to "
    + "them, net insulin) and the state at the event (glucose, carbs, minutes since the last carbs, IOB, time of day), "
    + "with the least-squares line of each figure against the value the chart lies along.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record EventOutcomesParameters : EventListParameters
{
    public new const string Name = "event_outcomes";

    [JsonIgnore]
    public override string DatasetName => Name;
}

/// <summary>A figure of each event, as the spread of a group counts it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<EventOutcome>))]
internal enum EventOutcome
{
    [JsonStringEnumMemberName("peakRise")] PeakRise,
    [JsonStringEnumMemberName("minutesToPeak")] MinutesToPeak,
    [JsonStringEnumMemberName("minutesBackToStart")] MinutesBackToStart,
    [JsonStringEnumMemberName("drop")] Drop,
    [JsonStringEnumMemberName("nadir")] [Description("Lowest reading")] Nadir,
    [JsonStringEnumMemberName("minutesToNadir")] [Description("Minutes to lowest")] MinutesToNadir,
    [JsonStringEnumMemberName("glucoseAt2Hours")] [Description("Glucose 2 hours after")] GlucoseAt2Hours,
    [JsonStringEnumMemberName("startGlucose")] [Description("Glucose at the event")] StartGlucose,
    [JsonStringEnumMemberName("netInsulin")] NetInsulin,
    [JsonStringEnumMemberName("gramsPerUnit")] [Description("Carbs per unit")] GramsPerUnit,
}

[Description(
    "event_outcome_spread: how one figure of each event is spread in each group: the events of each group counted in "
    + "ranges of the figure, drawn as bars, with each group's median and middle half.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record EventOutcomeSpreadParameters : EventListParameters
{
    public new const string Name = "event_outcome_spread";

    [JsonIgnore]
    public override string DatasetName => Name;

    [Description(
        "The figure to count. Peak rise, minutes to peak and minutes back to the start are figures of meals, unannounced "
        + "rises, sensor starts and high starts; drop, lowest reading and minutes to lowest of the other events.")]
    [DisplayName("Figure")]
    public EventOutcome Outcome { get; init; } = EventOutcome.PeakRise;
}

[Description("event_occurrence: when the events of each group happen: counted by local hour of the day or by local weekday.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record EventOccurrenceParameters : EventListParameters
{
    public new const string Name = "event_occurrence";

    [JsonIgnore]
    public override string DatasetName => Name;
}

/// <summary>Which setting a settings_evidence panel reads.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SettingsEvidenceSetting>))]
internal enum SettingsEvidenceSetting
{
    [JsonStringEnumMemberName("basal")] Basal,
    [Description("ISF")]
    [JsonStringEnumMemberName("isf")] Isf,
    [JsonStringEnumMemberName("carb_ratio")] CarbRatio,
}

[Description(
    "settings_evidence: the basal (per two-hour block), ISF and carb ratio (per time-of-day block) the user's own data "
    + "shows, beside the profile's values, each with its number of episodes, spread, confidence and rejections. "
    + "Observations from the data, not instructions.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SettingsEvidenceParameters : LoopAnalysisDatasetParameters
{
    public const string Name = "settings_evidence";

    [JsonIgnore]
    public override string DatasetName => Name;

    [Description("Only this setting: basal, isf or carb_ratio. Optional; all three when missing. A time_series of observed against profile needs one.")]
    public SettingsEvidenceSetting? Setting { get; init; }
}

[Description(
    "daily_profile: every local five-minute slot of the day across the range: glucose percentiles pooled across days, "
    + "the scheduled and delivered basal, net loop correction, IOB, COB and the target range.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record DailyProfileParameters : LoopAnalysisDatasetParameters
{
    public const string Name = "daily_profile";

    [JsonIgnore]
    public override string DatasetName => Name;

    [Description("Only these local weekdays. Optional; every day when missing.")]
    [DisplayName("Weekdays")]
    [Length(1, 7)]
    public IReadOnlyList<ReportWeekday>? DayOfWeek { get; init; }

    [JsonIgnore]
    public override IReadOnlyList<ReportWeekday>? Weekdays => DayOfWeek;

    [Description("Only the days that had something, or every other day. Optional; every day when missing.")]
    public ReportDayFilter? Days { get; init; }
}

[Description(
    "glucose_distribution_by_hour: for each local hour of the day, the glucose percentiles across the range and the "
    + "percent of time in each glucose range.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record GlucoseDistributionByHourParameters : ReportDatasetParameters
{
    public const string Name = "glucose_distribution_by_hour";

    [JsonIgnore]
    public override string DatasetName => Name;

    [Description("Only readings on these local weekdays. Optional; every day when missing.")]
    [DisplayName("Weekdays")]
    [Length(1, 7)]
    public IReadOnlyList<ReportWeekday>? DayOfWeek { get; init; }
}
