using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Services;

/// <summary>
/// Response for GET /api/v4/year-overview/gri-timeline
/// </summary>
public class GriTimelineResponse
{
    [JsonPropertyName("year")]
    public int Year { get; set; }

    [JsonPropertyName("periods")]
    public GriTimelinePeriod[] Periods { get; set; } = [];
}

/// <summary>
/// GRI data for a single month period
/// </summary>
public class GriTimelinePeriod
{
    [JsonPropertyName("periodStart")]
    public string PeriodStart { get; set; } = string.Empty;

    [JsonPropertyName("periodEnd")]
    public string PeriodEnd { get; set; } = string.Empty;

    [JsonPropertyName("gri")]
    public GlycemicRiskIndex Gri { get; set; } = new();

    [JsonPropertyName("averageGlucoseMgdl")]
    public double? AverageGlucoseMgdl { get; set; }

    [JsonPropertyName("totalDailyDose")]
    public double? TotalDailyDose { get; set; }

    [JsonPropertyName("averageDailyCarbs")]
    public double? AverageDailyCarbs { get; set; }

    [JsonPropertyName("readingCount")]
    public int ReadingCount { get; set; }
}

/// <summary>
/// Response for GET /api/v4/year-overview/years
/// </summary>
public class DataOverviewYearsResponse
{
    [JsonPropertyName("years")]
    public int[] Years { get; set; } = [];

    [JsonPropertyName("availableDataSources")]
    public string[] AvailableDataSources { get; set; } = [];
}

/// <summary>
/// Response for GET /api/v4/year-overview/daily-summary
/// </summary>
public class DailySummaryResponse
{
    [JsonPropertyName("year")]
    public int Year { get; set; }

    [JsonPropertyName("dataSources")]
    public string[]? DataSources { get; set; }

    [JsonPropertyName("days")]
    public DailySummaryDay[] Days { get; set; } = [];
}

/// <summary>
/// Response for GET /api/v4/year-overview/ehba1c-timeline
/// </summary>
public class EHbA1cTimelineResponse
{
    [JsonPropertyName("year")]
    public int Year { get; set; }

    [JsonPropertyName("points")]
    public EHbA1cPoint[] Points { get; set; } = [];
}

/// <summary>
/// One day's estimated HbA1c, derived from a recency-weighted average of the trailing 90-day
/// glucose window. Days with too little trailing data are omitted rather than emitted with a
/// low-confidence guess — see <see cref="IDataOverviewService.GetEHbA1cTimelineAsync"/>.
/// </summary>
public class EHbA1cPoint
{
    [JsonPropertyName("date")]
    public string Date { get; set; } = string.Empty;

    [JsonPropertyName("estimatedA1cPercent")]
    public double EstimatedA1cPercent { get; set; }

    /// <summary>ADAG estimate using linear daily weights 90 through 1 over 90 days.</summary>
    [JsonPropertyName("linear90DayPercent")]
    public double? Linear90DayPercent { get; set; }

    /// <summary>ADAG estimate using exponential weighting with a 30-day half-life.</summary>
    [JsonPropertyName("halfLife30DayPercent")]
    public double? HalfLife30DayPercent { get; set; }

    /// <summary>ADAG estimate using an unweighted mean over the trailing 90 days.</summary>
    [JsonPropertyName("unweighted90DayPercent")]
    public double? Unweighted90DayPercent { get; set; }

    /// <summary>ADAG estimate using a 120-day 50/25/25 recency model.</summary>
    [JsonPropertyName("weighted120DayPercent")]
    public double? Weighted120DayPercent { get; set; }

    /// <summary>ADAG estimate using an unweighted mean over the trailing 14 days.</summary>
    [JsonPropertyName("unweighted14DayPercent")]
    public double? Unweighted14DayPercent { get; set; }

    [JsonPropertyName("weightedAverageGlucoseMgdl")]
    public double WeightedAverageGlucoseMgdl { get; set; }

    [JsonPropertyName("readingCount")]
    public int ReadingCount { get; set; }

    [JsonPropertyName("daysWithData")]
    public int DaysWithData { get; set; }
}

/// <summary>
/// Aggregated data for a single day
/// </summary>
public class DailySummaryDay
{
    [JsonPropertyName("date")]
    public string Date { get; set; } = string.Empty;

    [JsonPropertyName("averageGlucoseMgdl")]
    public double? AverageGlucoseMgdl { get; set; }

    [JsonPropertyName("totalBolusUnits")]
    public double? TotalBolusUnits { get; set; }

    [JsonPropertyName("totalBasalUnits")]
    public double? TotalBasalUnits { get; set; }

    [JsonPropertyName("totalDailyDose")]
    public double? TotalDailyDose { get; set; }

    [JsonPropertyName("totalCarbs")]
    public double? TotalCarbs { get; set; }

    [JsonPropertyName("timeInRangePercent")]
    public double? TimeInRangePercent { get; set; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }

    /// <summary>
    /// Record counts keyed by SyncDataType name (e.g., "Glucose", "Boluses", "StateSpans")
    /// </summary>
    [JsonPropertyName("counts")]
    public Dictionary<string, int> Counts { get; set; } = new();
}
