using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Nocturne.Core.Models.LoopAnalysis;

namespace Nocturne.API.Controllers.V4.Analytics;

/// <summary>
/// The Meals &amp; events cohorts and window, bound beside <see cref="LoopAnalysisQuery"/> as
/// camelCase scalars. Each of up to <see cref="EventResponseDefinition.MaxCohorts"/> cohorts is
/// its parameters suffixed with its number: <c>anchor1</c>, <c>fromMinute1</c>, <c>minGrams1</c>.
/// With no anchor named, cohort 1 is every meal.
/// </summary>
public sealed class EventResponseQuery
{
    [FromQuery(Name = "anchor1")] public EventAnchorKind? Anchor1 { get; set; }
    [FromQuery(Name = "anchor2")] public EventAnchorKind? Anchor2 { get; set; }
    [FromQuery(Name = "anchor3")] public EventAnchorKind? Anchor3 { get; set; }

    /// <summary>Start of cohort 1's local time-of-day window, minutes after midnight; see <see cref="EventCohortDefinition.FromMinute"/>.</summary>
    [FromQuery(Name = "fromMinute1"), Range(0, MinuteOfDayMax)] public int? FromMinute1 { get; set; }
    [FromQuery(Name = "fromMinute2"), Range(0, MinuteOfDayMax)] public int? FromMinute2 { get; set; }
    [FromQuery(Name = "fromMinute3"), Range(0, MinuteOfDayMax)] public int? FromMinute3 { get; set; }

    /// <summary>End of cohort 1's window, exclusive; before the start, the window wraps past midnight.</summary>
    [FromQuery(Name = "toMinute1"), Range(0, MinuteOfDayMax)] public int? ToMinute1 { get; set; }
    [FromQuery(Name = "toMinute2"), Range(0, MinuteOfDayMax)] public int? ToMinute2 { get; set; }
    [FromQuery(Name = "toMinute3"), Range(0, MinuteOfDayMax)] public int? ToMinute3 { get; set; }

    /// <summary>Smallest meal in cohort 1, g; for a meal cohort only. Outside 0 to <see cref="EventResponseDefinition.MaxGrams"/>, ignored.</summary>
    [FromQuery(Name = "minGrams1")] public double? MinGrams1 { get; set; }
    [FromQuery(Name = "minGrams2")] public double? MinGrams2 { get; set; }
    [FromQuery(Name = "minGrams3")] public double? MinGrams3 { get; set; }

    /// <summary>Largest meal in cohort 1, g; for a meal cohort only. Outside 0 to <see cref="EventResponseDefinition.MaxGrams"/>, ignored.</summary>
    [FromQuery(Name = "maxGrams1")] public double? MaxGrams1 { get; set; }
    [FromQuery(Name = "maxGrams2")] public double? MaxGrams2 { get; set; }
    [FromQuery(Name = "maxGrams3")] public double? MaxGrams3 { get; set; }

    /// <summary>Drop events with another meal, correction bolus or rise in their window; ignored without treatments.read.</summary>
    [FromQuery(Name = "isolatedOnly")] public bool IsolatedOnly { get; set; }

    /// <summary>Count an unannounced rise only where the loop answered it with its own correction; ignored without treatments.read.</summary>
    [FromQuery(Name = "requireCorroboration")] public bool RequireCorroboration { get; set; }

    /// <summary>Minutes of window before the anchor, one of <see cref="EventResponseDefinition.MinutesBeforeChoices"/>; any other is ignored.</summary>
    [FromQuery(Name = "minutesBefore")] public int? MinutesBefore { get; set; }

    /// <summary>Minutes of window after the anchor, one of <see cref="EventResponseDefinition.MinutesAfterChoices"/>; any other is ignored.</summary>
    [FromQuery(Name = "minutesAfter")] public int? MinutesAfter { get; set; }

    /// <summary>The last minute of the day a time-of-day window may name.</summary>
    private const int MinuteOfDayMax = 24 * 60 - 1;

    private static bool IsGrams(double? grams) => grams is null or (>= 0 and <= EventResponseDefinition.MaxGrams);

    private int AppliedMinutesBefore =>
        MinutesBefore is { } m && EventResponseDefinition.MinutesBeforeChoices.Contains(m) ? m : EventResponseDefinition.DefaultMinutesBefore;

    private int AppliedMinutesAfter =>
        MinutesAfter is { } m && EventResponseDefinition.MinutesAfterChoices.Contains(m) ? m : EventResponseDefinition.DefaultMinutesAfter;

    private IEnumerable<(int Cohort, EventAnchorKind? Anchor, int? From, int? To, double? MinGrams, double? MaxGrams)> Cohorts() =>
    [
        (1, Anchor1, FromMinute1, ToMinute1, MinGrams1, MaxGrams1),
        (2, Anchor2, FromMinute2, ToMinute2, MinGrams2, MaxGrams2),
        (3, Anchor3, FromMinute3, ToMinute3, MinGrams3, MaxGrams3),
    ];

    /// <summary>The cohort's anchor: the one named, or every meal for cohort 1 when none is.</summary>
    private EventAnchorKind? KindOf(int cohort, EventAnchorKind? anchor) =>
        anchor ?? (cohort == 1 && Anchor1 is null && Anchor2 is null && Anchor3 is null ? EventAnchorKind.Meal : null);

    /// <summary>
    /// Why the parameters do not make a definition, or null when they do. The ranges are the
    /// <see cref="RangeAttribute"/>s'; what is left is what no reading could make sense of.
    /// </summary>
    public string? Problem()
    {
        foreach (var (n, anchor, from, to, min, max) in Cohorts())
        {
            var kind = KindOf(n, anchor);
            if (kind is null && (from ?? to ?? min ?? max) is not null)
                return $"Cohort {n}'s filters need anchor{n}.";
            if ((min ?? max) is not null && kind != EventAnchorKind.Meal)
                return $"minGrams{n} and maxGrams{n} apply only to a meal cohort.";
        }
        return null;
    }

    /// <summary>
    /// The window and grams parameters that were sent with a value outside
    /// <see cref="EventResponseLimits"/>; the definition applies the default in their place.
    /// </summary>
    public IEnumerable<string> OutOfLimits()
    {
        if (MinutesBefore is not null && AppliedMinutesBefore != MinutesBefore)
            yield return "minutesBefore";
        if (MinutesAfter is not null && AppliedMinutesAfter != MinutesAfter)
            yield return "minutesAfter";
        foreach (var (n, _, _, _, min, max) in Cohorts())
        {
            if (!IsGrams(min))
                yield return $"minGrams{n}";
            if (!IsGrams(max))
                yield return $"maxGrams{n}";
        }
    }

    /// <summary>
    /// The definition asked for, before the caller's scopes narrow it. A window whose start and end
    /// are the same minute is the whole day, grams given the wrong way round are swapped, and a
    /// value <see cref="OutOfLimits"/> names is left at its default.
    /// </summary>
    public EventResponseDefinition ToDefinition() => new()
    {
        Cohorts =
        [
            .. Cohorts()
                .Select(c => c with { Anchor = KindOf(c.Cohort, c.Anchor) })
                .Where(c => c.Anchor is not null)
                .Select(c => c with
                {
                    MinGrams = IsGrams(c.MinGrams) ? c.MinGrams : null,
                    MaxGrams = IsGrams(c.MaxGrams) ? c.MaxGrams : null,
                })
                .Select(c =>
                {
                    var allDay = c.From is not null && c.From == c.To;
                    var swap = c.MinGrams > c.MaxGrams;
                    return new EventCohortDefinition
                    {
                        Cohort = c.Cohort,
                        Anchor = c.Anchor!.Value,
                        FromMinute = allDay ? null : c.From,
                        ToMinute = allDay ? null : c.To,
                        MinGrams = swap ? c.MaxGrams : c.MinGrams,
                        MaxGrams = swap ? c.MinGrams : c.MaxGrams,
                    };
                }),
        ],
        MinutesBefore = AppliedMinutesBefore,
        MinutesAfter = AppliedMinutesAfter,
        IsolatedOnly = IsolatedOnly,
        RequireCorroboration = RequireCorroboration,
    };
}
