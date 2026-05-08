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

    /// <summary>Approximate count of CGM readings for the day (5-min cadence assumed).</summary>
    public int TotalReadings { get; set; }
    public int InRangeCount { get; set; }
    public int LowCount { get; set; }
    public int HighCount { get; set; }

    public double InRangePercent { get; set; }
    public double LowPercent { get; set; }
    public double HighPercent { get; set; }

    /// <summary>Mean glucose across the in-range and low ranges, mg/dL.</summary>
    public double AverageGlucose { get; set; }

    public double TotalCarbs { get; set; }
    public double TotalInsulin { get; set; }
    public double TotalBolus { get; set; }
    public double TotalBasal { get; set; }
    public double CarbToInsulinRatio { get; set; }

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
    public double LowPercent { get; set; }
    public double HighPercent { get; set; }
    public double AvgGlucose { get; set; }
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
}
