namespace Nocturne.Core.Models;

/// <summary>
/// One day of aggregated stats for the punch-card calendar view: TIR + treatment totals + raw entries.
/// </summary>
public class PunchCardDay
{
    /// <summary>Date string in YYYY-MM-DD form.</summary>
    public string Date { get; set; } = "";

    /// <summary>Local-midnight Unix milliseconds for the day.</summary>
    public long Timestamp { get; set; }

    /// <summary>Count of the day's CGM readings.</summary>
    public int TotalReadings { get; set; }
    public int InRangeCount { get; set; }
    public int VeryLowCount { get; set; }
    public int LowCount { get; set; }
    public int HighCount { get; set; }
    public int VeryHighCount { get; set; }

    public double InRangePercent { get; set; }

    /// <summary>Share of readings below the very-low threshold.</summary>
    public double VeryLowPercent { get; set; }

    /// <summary>Share of readings from the very-low threshold up to the low threshold.</summary>
    public double LowPercent { get; set; }

    /// <summary>Share of readings above the high threshold up to the very-high threshold.</summary>
    public double HighPercent { get; set; }

    /// <summary>Share of readings above the very-high threshold.</summary>
    public double VeryHighPercent { get; set; }

    /// <summary>Mean of every reading that day, mg/dL.</summary>
    public double AverageGlucose { get; set; }

    public double TotalCarbs { get; set; }
    public double TotalInsulin { get; set; }
    public double TotalBolus { get; set; }
    public double TotalBasal { get; set; }
    /// <inheritdoc cref="TreatmentSummary.CarbToInsulinRatio"/>
    public double? CarbToInsulinRatio { get; set; }

    /// <summary>Sorted glucose readings for the day (mills + mg/dL only) for the profile-line view.</summary>
    public List<PunchCardEntry> Entries { get; set; } = new();
}

/// <summary>One CGM reading point in the punch-card day profile.</summary>
public class PunchCardEntry
{
    public long Mills { get; set; }
    public double Mgdl { get; set; }
}

/// <summary>Month-level summary aggregated across the days that had data.</summary>
public class PunchCardMonthSummary
{
    public int DayCount { get; set; }
    public int TotalReadings { get; set; }
    public double InRangePercent { get; set; }
    public double VeryLowPercent { get; set; }
    public double LowPercent { get; set; }
    public double HighPercent { get; set; }
    public double VeryHighPercent { get; set; }
    public double AvgGlucose { get; set; }

    /// <summary>
    /// Total daily dose in units: the month's insulin over the calendar days the requested range
    /// covers in it up to and including the tenant's today, whether or not the sensor read on those
    /// days. Null when no insulin was recorded. Absent with the whole summary for a month with no readings.
    /// </summary>
    public double? Tdd { get; set; }

    /// <summary>
    /// Carbs per calendar day over the same days as <see cref="Tdd"/>. Null when no carbs were recorded.
    /// </summary>
    public double? AvgDailyCarbs { get; set; }
}

/// <summary>One month bucket within a punch-card response — days plus per-month maxes for chart scaling.</summary>
public class PunchCardMonth
{
    public int Year { get; set; }
    public int Month { get; set; }
    public string MonthName { get; set; } = "";
    public List<PunchCardDay> Days { get; set; } = new();

    public double MaxCarbs { get; set; }
    public double MaxInsulin { get; set; }
    public double MaxCarbInsulinDiff { get; set; }
    public int TotalReadings { get; set; }

    /// <summary>Null until populated; null also means no days had data.</summary>
    public PunchCardMonthSummary? Summary { get; set; }
}

/// <summary>The date window covered by the response, normalised to day boundaries.</summary>
public class PunchCardDateRange
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
}

/// <summary>
/// Response for <c>GET /api/v4/Statistics/punch-card</c>: pre-aggregated month-by-day statistics
/// for the calendar view, computed entirely server-side without per-day round-trips.
/// </summary>
public class PunchCardResponse
{
    public List<PunchCardMonth> Months { get; set; } = new();
    public PunchCardDateRange DateRange { get; set; } = new();

    /// <summary>Maxes across all months, used by the frontend to pick consistent chart scales.</summary>
    public double GlobalMaxCarbs { get; set; }
    public double GlobalMaxInsulin { get; set; }
    public double GlobalMaxCarbInsulinDiff { get; set; }

    /// <summary>Why <see cref="Months"/> is empty; null when the tenant's timezone resolved.</summary>
    public TimeZoneUnavailableReason? TimeZoneUnavailableReason { get; set; }
}
