using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Nocturne.API.Controllers.V4.Analytics;
using Nocturne.Core.Models.LoopAnalysis;

namespace Nocturne.API.Mcp.Reports;

/// <summary>
/// The masks every Loop Analysis dataset takes, as <see cref="LoopAnalysisQuery"/> names them. A
/// mask the token lacks the scope for is dropped and named in the panel summary.
/// </summary>
internal abstract record LoopAnalysisDatasetParameters : ReportDatasetParameters
{
    public const int MaxProfileNameLength = 100;

    [Description("Leave out time an override ran. Default false. Needs treatments.read.")]
    public bool ExcludeOverrides { get; init; }

    [Description("Leave out time a temporary target ran. Default false. Needs treatments.read.")]
    public bool ExcludeTemporaryTargets { get; init; }

    [Description("Leave out exercise time. Default false. Needs treatments.read.")]
    public bool ExcludeExercise { get; init; }

    [Description("Leave out illness time. Default true. Needs treatments.read.")]
    public bool ExcludeIllness { get; init; } = true;

    [Description("Leave out time the pump was in manual mode or had no AID algorithm. Default true. Needs devices.read.")]
    public bool ExcludeOpenLoop { get; init; } = true;

    [Description("Analyze only time this profile was active. Optional. Needs therapy.read.")]
    [StringLength(MaxProfileNameLength)]
    public string? ProfileName { get; init; }

    /// <summary>The query for local dates <paramref name="start"/> to <paramref name="end"/>, with these masks.</summary>
    public LoopAnalysisQuery ToQuery(DateOnly start, DateOnly end) => new()
    {
        StartDate = start,
        EndDate = end,
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

[Description("A local time-of-day window, minutes after midnight. With 'toMinute' before 'fromMinute' it wraps past midnight.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ReportTimeOfDay
{
    [Description("Start, minutes after local midnight, 0 to 1439.")]
    [Range(0, 1439)]
    [JsonRequired]
    public int FromMinute { get; init; }

    [Description("End, exclusive, minutes after local midnight, 0 to 1439.")]
    [Range(0, 1439)]
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
    "One cohort of events: an anchor kind, narrowed by time of day, weekday and, for meals, by size and by the foods "
    + "the meal carries a food record of.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ReportEventCohort
{
    [Description(
        "What the events are aligned on: Meal (a carb entry), CorrectionBolus (a manual bolus with no meal near it), "
        + "SensorStart, ExerciseStart, OverrideStart, TemporaryTargetStart, or UnannouncedRise (a meal-like rise with no carbs logged).")]
    [JsonRequired]
    public EventAnchorKind Anchor { get; init; }

    [Description("Only events in this local time-of-day window. Optional.")]
    public ReportTimeOfDay? TimeOfDay { get; init; }

    [Description("Meal cohorts only: only meals of this size. Optional.")]
    public ReportGrams? Grams { get; init; }

    [Description("Only events on these local weekdays. Optional; every day when missing.")]
    [Length(1, 7)]
    public IReadOnlyList<ReportWeekday>? DayOfWeek { get; init; }

    [Description(
        "Meal cohorts only: food ids (from find_foods); a meal matches when it carries a food record of any of them. "
        + "Meals with carbs only and no food record cannot match and are counted apart. Needs food.read.")]
    [Length(1, EventResponseDefinition.MaxFoods)]
    public IReadOnlyList<Guid>? Foods { get; init; }

    [Description("Meal cohorts only: a food category (from find_foods); a meal matches when it carries a food record of that category. Needs food.read.")]
    [StringLength(EventResponseDefinition.MaxFoodCategoryLength)]
    public string? FoodCategory { get; init; }

    [JsonIgnore]
    public bool HasFoodFilter => Foods is { Count: > 0 } || !string.IsNullOrWhiteSpace(FoodCategory);
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
    "event_response: glucose, glucose change, IOB, COB and net loop correction aligned on up to three cohorts of events "
    + "(meals, correction boluses, sensor starts, exercise, overrides, temporary targets, unannounced rises), each spread "
    + "across its events as percentiles in five-minute slots from the event; and each event with its figures.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record EventResponseParameters : LoopAnalysisDatasetParameters
{
    public const string Name = "event_response";

    [JsonIgnore]
    public override string DatasetName => Name;

    [Description("The cohorts, 1 to 3. Optional; every meal when missing.")]
    [Length(1, EventResponseDefinition.MaxCohorts)]
    public IReadOnlyList<ReportEventCohort>? Cohorts { get; init; }

    [Description("Minutes of window before each event: 0, 30, 60, 90 or 120. Default 60.")]
    [Range(0, 120)]
    public int MinutesBefore { get; init; } = EventResponseDefinition.DefaultMinutesBefore;

    [Description("Minutes of window after each event: 120, 180, 240, 300, 360, 420 or 480. Default 300.")]
    [Range(120, 480)]
    public int MinutesAfter { get; init; } = EventResponseDefinition.DefaultMinutesAfter;

    [Description("Only events with no other meal, correction bolus or unannounced rise in their window. Default false. Needs treatments.read.")]
    public bool IsolatedOnly { get; init; }

    [Description("Count an unannounced rise only where the loop answered it with extra insulin. Default false. Needs treatments.read.")]
    public bool RequireCorroboration { get; init; }

    [Description("A second range of local dates to compare with, drawn as its own series. Optional.")]
    public ReportDateRange? ComparisonRange { get; init; }

    /// <summary>The cohorts asked for, or every meal.</summary>
    [JsonIgnore]
    public IReadOnlyList<ReportEventCohort> AppliedCohorts =>
        Cohorts is { Count: > 0 } cohorts ? cohorts : [new ReportEventCohort { Anchor = EventAnchorKind.Meal }];

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

/// <summary>Which setting a settings_evidence panel reads.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SettingsEvidenceSetting>))]
internal enum SettingsEvidenceSetting
{
    [JsonStringEnumMemberName("basal")] Basal,
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
}
