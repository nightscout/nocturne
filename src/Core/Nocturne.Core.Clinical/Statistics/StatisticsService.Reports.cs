using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Basal;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Clinical.Statistics;

public partial class StatisticsService
{
    private const int MinimumPeriodReadings = 10;

    private static readonly string[] MonthNames =
    [
        "January", "February", "March", "April", "May", "June",
        "July", "August", "September", "October", "November", "December"
    ];

    /// <inheritdoc />
    public IEnumerable<ContributingDevice> CalculateContributingDevices(
        IReadOnlyCollection<SensorGlucose> readings,
        IEnumerable<PatientDevice> devices)
    {
        var countByDevice = new Dictionary<Guid, int>();
        var unattributedCount = 0;
        foreach (var r in readings)
        {
            if (r.PatientDeviceId is { } id)
                countByDevice[id] = countByDevice.GetValueOrDefault(id) + 1;
            else
                unattributedCount++;
        }

        foreach (var d in devices.Where(d => d.DeviceCategory == DeviceCategory.CGM))
        {
            if (!countByDevice.TryGetValue(d.Id, out var readingCount) || readingCount == 0) continue;

            yield return new ContributingDevice
            {
                PatientDeviceId = d.Id,
                Name = d.DisplayName(),
                ReadingCount = readingCount,
            };
        }

        if (unattributedCount > 0)
        {
            yield return new ContributingDevice
            {
                PatientDeviceId = null,
                Name = null,
                ReadingCount = unattributedCount,
            };
        }
    }

    /// <summary>Whether a period's readings are enough to compute its analytics.</summary>
    public static bool HasSufficientPeriodData(int entryCount) => entryCount >= MinimumPeriodReadings;

    /// <inheritdoc />
    public PeriodStatistics CalculatePeriodStatistics(
        int periodDays,
        DateTime startDate,
        DateTime endDate,
        IReadOnlyCollection<SensorGlucose> entries,
        IReadOnlyCollection<Bolus> manualBoluses,
        IReadOnlyCollection<Bolus> algorithmBoluses,
        IReadOnlyCollection<TempBasal> tempBasals,
        IReadOnlyCollection<BasalInjection> basalInjections,
        IReadOnlyCollection<CarbIntake> carbs,
        IReadOnlyCollection<DeviceEvent> deviceEvents,
        long untilMills,
        TimeZoneInfo? timeZone)
    {
        var statistics = new PeriodStatistics
        {
            PeriodDays = periodDays,
            StartDate = startDate,
            EndDate = endDate,
            HasSufficientData = HasSufficientPeriodData(entries.Count),
            EntryCount = entries.Count,
            TreatmentCount = manualBoluses.Count + carbs.Count,
        };

        if (!statistics.HasSufficientData) return statistics;

        var analytics = AnalyzeGlucoseData(
            entries,
            manualBoluses,
            carbs,
            deviceEvents: deviceEvents,
            windowDays: periodDays,
            timeZone: timeZone);

        statistics.Analytics = analytics;
        statistics.TreatmentSummary = CalculateTreatmentSummary(manualBoluses, carbs, dayCount: periodDays);
        statistics.InsulinDelivery = CalculateInsulinDeliveryStatistics(
            manualBoluses,
            algorithmBoluses,
            tempBasals,
            carbs,
            startDate,
            endDate,
            untilMills,
            basalInjections);
        statistics.Reliability = analytics.Reliability;
        statistics.Gmi = CalculateGMI(analytics.BasicStats.Mean);
        statistics.Gmi.Reliability = analytics.Reliability;
        return statistics;
    }

    /// <summary>
    /// The <c>untilMills</c> of <see cref="IStatisticsService.CalculateInsulinDeliveryStatistics"/>
    /// for a window ending at <paramref name="endUtc"/>. A window that ends in the future has not
    /// delivered past <paramref name="now"/>.
    /// </summary>
    public static long UntilMills(DateTime endUtc, DateTimeOffset now) =>
        Math.Min(
            endUtc.ToMills(),
            now.ToUnixTimeMilliseconds());

    /// <summary>
    /// The UTC span of the calendar days <paramref name="startDate"/> to <paramref name="endDate"/>
    /// inclusive on the tenant's clock, ending one tick before the next local midnight.
    /// </summary>
    public static (DateTime StartUtc, DateTime EndUtc) PunchCardRange(
        DateOnly startDate, DateOnly endDate, TimeZoneInfo timeZone) =>
        (
            TimeZoneInfo.ConvertTimeToUtc(startDate.ToDateTime(TimeOnly.MinValue), timeZone),
            TimeZoneInfo.ConvertTimeToUtc(endDate.ToDateTime(TimeOnly.MinValue).AddDays(1).AddTicks(-1), timeZone)
        );

    /// <inheritdoc />
    public PunchCardResponse CalculatePunchCard(
        IEnumerable<SensorGlucose> glucose,
        IEnumerable<Bolus> manualBoluses,
        IEnumerable<Bolus> algorithmBoluses,
        IEnumerable<CarbIntake> carbs,
        IEnumerable<TempBasal> tempBasals,
        IEnumerable<BasalInjection> basalInjections,
        DateOnly startDate,
        DateOnly endDate,
        TimeZoneInfo timeZone,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var startLocalDate = startDate.ToDateTime(TimeOnly.MinValue);
        var endLocalDate = endDate.ToDateTime(TimeOnly.MinValue);
        var (startUtc, endUtc) = PunchCardRange(startDate, endDate, timeZone);
        var untilMills = UntilMills(endUtc, now);

        var manual = manualBoluses.ToList();
        var algorithm = algorithmBoluses.ToList();

        var dailyBasalBolus = CalculateDailyBasalBolusRatios(
            manual, algorithm, tempBasals, startUtc, untilMills, timeZone, basalInjections);
        var dailyBasalMap = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var d in dailyBasalBolus.DailyData)
        {
            if (!string.IsNullOrEmpty(d.Date)) dailyBasalMap[d.Date] = d.Basal;
        }

        var days = new List<DateTime>();
        for (var day = startLocalDate.Date; day <= endLocalDate.Date; day = day.AddDays(1)) days.Add(day);
        var windows = days.Select(d => PunchCardDayWindow.For(d, timeZone)).ToList();
        var glucoseByDay = PunchCardDayBuckets.Bucket(glucose, windows, e => e.Mills);
        var bolusesByDay = PunchCardDayBuckets.Bucket(manual, windows, b => b.Mills);
        var algorithmBolusesByDay = PunchCardDayBuckets.Bucket(algorithm, windows, b => b.Mills);
        var carbsByDay = PunchCardDayBuckets.Bucket(carbs, windows, c => c.Mills);

        var monthsMap = new Dictionary<string, PunchCardMonth>(StringComparer.Ordinal);
        var coveredMinutesByDate = new Dictionary<string, double>(StringComparer.Ordinal);
        for (var i = 0; i < days.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var day = days[i];

            var monthKey = $"{day.Year}-{day.Month - 1}";
            if (!monthsMap.TryGetValue(monthKey, out var monthBucket))
            {
                monthBucket = new PunchCardMonth
                {
                    Year = day.Year,
                    Month = day.Month - 1,
                    MonthName = MonthNames[day.Month - 1],
                };
                monthsMap[monthKey] = monthBucket;
            }

            var (dayStats, coveredMinutes) = PunchCardDayStatistics(
                day,
                windows[i].StartMs,
                glucoseByDay[i],
                bolusesByDay[i],
                algorithmBolusesByDay[i],
                carbsByDay[i],
                dailyBasalMap);

            monthBucket.Days.Add(dayStats);
            coveredMinutesByDate[dayStats.Date] = coveredMinutes;
            monthBucket.MaxCarbs = Math.Max(monthBucket.MaxCarbs, dayStats.TotalCarbs);
            monthBucket.MaxInsulin = Math.Max(monthBucket.MaxInsulin, dayStats.TotalInsulin);
            monthBucket.MaxCarbInsulinDiff = Math.Max(
                monthBucket.MaxCarbInsulinDiff, Math.Abs(dayStats.CarbToInsulinRatio ?? 0));
            monthBucket.TotalReadings += dayStats.TotalReadings;
        }

        var today = TimeZoneInfo.ConvertTime(now, timeZone).ToString("yyyy-MM-dd");
        foreach (var month in monthsMap.Values)
            month.Summary = SummariseMonth(month, today, coveredMinutesByDate);

        var months = monthsMap.Values
            .OrderBy(m => m.Year).ThenBy(m => m.Month)
            .ToList();

        return new PunchCardResponse
        {
            Months = months,
            DateRange = new PunchCardDateRange
            {
                From = startUtc.ToString("o"),
                To = endUtc.ToString("o"),
            },
            GlobalMaxCarbs = months.Count > 0 ? months.Max(m => m.MaxCarbs) : 0,
            GlobalMaxInsulin = months.Count > 0 ? months.Max(m => m.MaxInsulin) : 0,
            GlobalMaxCarbInsulinDiff = months.Count > 0 ? months.Max(m => m.MaxCarbInsulinDiff) : 0,
        };
    }

    private (PunchCardDay Day, double CoveredMinutes) PunchCardDayStatistics(
        DateTime day,
        long dayStartMs,
        IReadOnlyList<SensorGlucose> dayEntries,
        IReadOnlyList<Bolus> dayBoluses,
        IReadOnlyList<Bolus> dayAlgorithmBoluses,
        IReadOnlyList<CarbIntake> dayCarbs,
        IReadOnlyDictionary<string, double> dailyBasal)
    {
        TimeInRangeMetrics? tir = null;
        double coveredMinutes = 0;
        if (dayEntries.Count > 0)
            (tir, coveredMinutes) = TimeInRangeOverCoverage(dayEntries);
        TreatmentSummary? treatment = dayBoluses.Count > 0 || dayCarbs.Count > 0
            ? CalculateTreatmentSummary(dayBoluses, dayCarbs)
            : null;

        var pct = tir?.Percentages;
        var inRangePct = pct?.Target ?? 0;
        var veryLowPct = pct?.VeryLow ?? 0;
        var lowPct = pct?.Low ?? 0;
        var highPct = pct?.High ?? 0;
        var veryHighPct = pct?.VeryHigh ?? 0;

        var dateStr = day.ToString("yyyy-MM-dd");
        var totals = treatment?.Totals;
        var totalBolus = (totals?.Insulin?.Bolus ?? 0)
            + CountedBoluses(dayAlgorithmBoluses).Sum(b => b.Insulin);
        var totalBasal = dailyBasal.GetValueOrDefault(dateStr, 0.0);

        var entries = dayEntries
            .Where(e => GlucoseStatistics.IsPlausibleReading(e.Mgdl))
            .OrderBy(e => e.Mills)
            .Select(e => new PunchCardEntry { Mills = e.Mills, Mgdl = e.Mgdl })
            .ToList();

        var totalReadings = entries.Count;

        var punchCardDay = new PunchCardDay
        {
            Date = dateStr,
            Timestamp = dayStartMs,
            TotalReadings = totalReadings,
            InRangeCount = ReadingsAt(inRangePct, totalReadings),
            VeryLowCount = ReadingsAt(veryLowPct, totalReadings),
            LowCount = ReadingsAt(lowPct, totalReadings),
            HighCount = ReadingsAt(highPct, totalReadings),
            VeryHighCount = ReadingsAt(veryHighPct, totalReadings),
            InRangePercent = inRangePct,
            VeryLowPercent = veryLowPct,
            LowPercent = lowPct,
            HighPercent = highPct,
            VeryHighPercent = veryHighPct,
            AverageGlucose = totalReadings > 0 ? entries.Average(e => e.Mgdl) : 0,
            TotalCarbs = totals?.Food?.Carbs ?? 0,
            TotalInsulin = totalBolus + totalBasal,
            TotalBolus = totalBolus,
            TotalBasal = totalBasal,
            CarbToInsulinRatio = treatment?.CarbToInsulinRatio,
            Entries = entries,
        };

        return (punchCardDay, coveredMinutes);
    }

    private static int ReadingsAt(double percent, int totalReadings) =>
        (int)Math.Round(percent / 100.0 * totalReadings);

    /// <summary>
    /// The month's zone shares are the days' shares weighted by the minutes each day's readings
    /// cover, so they equal zone minutes over covered minutes across the month whatever cadence
    /// each day was recorded at. Average glucose is by reading, as it is per day.
    /// </summary>
    private static PunchCardMonthSummary? SummariseMonth(
        PunchCardMonth month,
        string today,
        IReadOnlyDictionary<string, double> coveredMinutesByDate)
    {
        var daysWithData = month.Days.Where(d => d.TotalReadings > 0).ToList();
        if (daysWithData.Count == 0) return null;

        var totalReadings = daysWithData.Sum(d => d.TotalReadings);
        var coveredMinutes = daysWithData.Sum(d => coveredMinutesByDate.GetValueOrDefault(d.Date));
        var totalInsulin = month.Days.Sum(d => d.TotalInsulin);
        var totalCarbs = month.Days.Sum(d => d.TotalCarbs);
        var elapsedDays = Math.Max(1, month.Days.Count(d => string.CompareOrdinal(d.Date, today) <= 0));

        double Share(Func<PunchCardDay, double> dayPercent) =>
            GlucoseCadence.PercentOfCovered(
                daysWithData.Sum(d => dayPercent(d) / 100 * coveredMinutesByDate.GetValueOrDefault(d.Date)),
                coveredMinutes);

        return new PunchCardMonthSummary
        {
            DayCount = daysWithData.Count,
            TotalReadings = totalReadings,
            InRangePercent = Share(d => d.InRangePercent),
            VeryLowPercent = Share(d => d.VeryLowPercent),
            LowPercent = Share(d => d.LowPercent),
            HighPercent = Share(d => d.HighPercent),
            VeryHighPercent = Share(d => d.VeryHighPercent),
            AvgGlucose = daysWithData.Sum(d => d.AverageGlucose * d.TotalReadings) / totalReadings,
            Tdd = totalInsulin > 0 ? totalInsulin / elapsedDays : null,
            AvgDailyCarbs = totalCarbs > 0 ? totalCarbs / elapsedDays : null,
        };
    }
}
