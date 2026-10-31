using System.Globalization;
using Microsoft.AspNetCore.WebUtilities;
using Nocturne.Core.Models.LoopAnalysis;

namespace Nocturne.API.Reports;

/// <summary>
/// The web report a panel opens as, with the panel's range and parameters in its query string, in
/// the names the web pages read them by. The report view hands it to the host's <c>ui/open-link</c>.
/// </summary>
internal static class ReportLinks
{
    /// <summary>The link for <paramref name="panel"/> on <paramref name="origin"/>, or null for a dataset with no matching page.</summary>
    public static string? For(string origin, ReportPanelSpec panel, ReportPanelResult result, string? zoneId)
    {
        var path = panel.Dataset switch
        {
            EventResponseParameters => "/reports/loop-analysis/meals-and-events",
            SettingsEvidenceParameters => "/reports/loop-analysis/settings-evidence",
            DailyProfileParameters => "/reports/loop-analysis/daily-profile",
            GlucoseDistributionByHourParameters => "/reports/glucose-distribution",
            GlucoseStatisticsParameters => "/reports/glucose-distribution",
            GlucoseSeriesParameters => "/reports/readings",
            TreatmentsParameters => "/reports/treatments",
            BasalDeliveryParameters => "/reports/basal-analysis",
            DeviceStatusParameters => "/reports/battery",
            GlucoseDistributionParameters => "/reports/agp",
            PeriodComparisonParameters => "/reports/comparison",
            _ => null,
        };
        if (path is null)
            return null;
        if (result.LinkQuery is { } own)
            return QueryHelpers.AddQueryString(origin.TrimEnd('/') + path, own);

        var zone = zoneId is not null && TimeZoneInfo.TryFindSystemTimeZoneById(zoneId, out var found) ? found : TimeZoneInfo.Utc;
        string Day(long mills) => DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(mills), zone).DateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var query = new List<KeyValuePair<string, string?>>
        {
            new("from", Day(result.Window.From)),
            new("to", Day(Math.Max(result.Window.From, result.Window.To - 1))),
        };

        if (panel.Dataset is LoopAnalysisDatasetParameters masks)
        {
            void Mask(string name, bool value, bool fallback)
            {
                if (value != fallback)
                    query.Add(new(name, value ? "true" : "false"));
            }
            Mask("excludeOverrides", masks.ExcludeOverrides, false);
            Mask("excludeTemporaryTargets", masks.ExcludeTemporaryTargets, false);
            Mask("excludeExercise", masks.ExcludeExercise, false);
            Mask("excludeIllness", masks.ExcludeIllness, true);
            Mask("excludeOpenLoop", masks.ExcludeOpenLoop, true);
            if (!string.IsNullOrWhiteSpace(masks.ProfileName))
                query.Add(new("profile", masks.ProfileName.Trim()));
        }

        if (panel.Dataset is EventResponseParameters events)
        {
            if (events.ComparisonRange is { } comparison)
            {
                query.Add(new("compare", "custom"));
                query.Add(new("compareFrom", comparison.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
                query.Add(new("compareTo", comparison.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
            }
            var definition = events.ToEventQuery(events.AppliedCohorts).ToDefinition();
            foreach (var cohort in definition.Cohorts)
                AddCohort(query, cohort);
            if (definition.IsolatedOnly) query.Add(new("isolatedOnly", "true"));
            if (definition.RequireCorroboration) query.Add(new("requireCorroboration", "true"));
            query.Add(new("minutesBefore", definition.MinutesBefore.ToString(CultureInfo.InvariantCulture)));
            query.Add(new("minutesAfter", definition.MinutesAfter.ToString(CultureInfo.InvariantCulture)));
        }

        return QueryHelpers.AddQueryString(origin.TrimEnd('/') + path, query);
    }

    private static void AddCohort(List<KeyValuePair<string, string?>> query, EventCohortDefinition cohort)
    {
        var n = cohort.Cohort.ToString(CultureInfo.InvariantCulture);
        string Text(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
        query.Add(new($"anchor{n}", cohort.Anchor.ToString()));
        if (cohort.FromMinute is { } from) query.Add(new($"fromMinute{n}", from.ToString(CultureInfo.InvariantCulture)));
        if (cohort.ToMinute is { } to) query.Add(new($"toMinute{n}", to.ToString(CultureInfo.InvariantCulture)));
        if (cohort.MinGrams is { } min) query.Add(new($"minGrams{n}", Text(min)));
        if (cohort.MaxGrams is { } max) query.Add(new($"maxGrams{n}", Text(max)));
        foreach (var food in cohort.FoodIds ?? [])
            query.Add(new($"foodIds{n}", food.ToString()));
        if (cohort.FoodCategory is { } category) query.Add(new($"foodCategory{n}", category));
        // The web report reads weekdays by number, as the API's DayOfWeek serialises.
        foreach (var day in cohort.DaysOfWeek ?? [])
            query.Add(new($"dayOfWeek{n}", ((int)day).ToString(CultureInfo.InvariantCulture)));
    }
}
