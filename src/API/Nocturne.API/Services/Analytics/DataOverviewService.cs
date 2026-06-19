using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Services;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Analytics;

/// <summary>
/// Service for aggregating data overview statistics across all data types.
/// Provides year-level availability and day-level <see cref="Entry"/> and <see cref="Treatment"/>
/// record counts for heatmap and calendar visualization in the dashboard.
/// All timestamps are resolved to the user's timezone via <see cref="ITherapySettingsResolver.GetTimezoneAsync"/>.
/// </summary>
/// <seealso cref="IDataOverviewService"/>
/// <seealso cref="IStatisticsService"/>
/// <seealso cref="ITherapySettingsResolver"/>
public class DataOverviewService : IDataOverviewService
{
    private readonly ITenantDbContextFactory _factory;
    private readonly ITherapySettingsResolver _therapySettingsResolver;
    private readonly IStatisticsService _statisticsService;
    private readonly ILogger<DataOverviewService> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="DataOverviewService"/>.
    /// </summary>
    /// <param name="factory">Tenant-scoped DbContext factory. Each query leases a context whose
    /// per-category share visibility is carried to Row-Level Security, so public shares see only
    /// the categories they were granted.</param>
    /// <param name="therapySettingsResolver">Resolver for the user's active timezone and therapy settings.</param>
    /// <param name="statisticsService">Statistics service for per-day metric aggregation.</param>
    /// <param name="logger">The logger instance.</param>
    public DataOverviewService(
        ITenantDbContextFactory factory,
        ITherapySettingsResolver therapySettingsResolver,
        IStatisticsService statisticsService,
        ILogger<DataOverviewService> logger
    )
    {
        _factory = factory;
        _therapySettingsResolver = therapySettingsResolver;
        _statisticsService = statisticsService;
        _logger = logger;
    }

    private async Task<TimeZoneInfo> GetUserTimeZoneAsync(CancellationToken cancellationToken = default)
    {
        var tzId = await _therapySettingsResolver.GetTimezoneAsync(ct: cancellationToken);
        return !string.IsNullOrEmpty(tzId)
            ? TimeZoneHelper.GetTimeZoneInfoFromId(tzId)
            : TimeZoneInfo.Utc;
    }

    /// <inheritdoc />
    public async Task<DataOverviewYearsResponse> GetAvailableYearsAsync(
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug("Getting available years for data overview");

        await using var context = await _factory.CreateAsync(cancellationToken);

        // Run all queries sequentially — DbContext is not thread-safe
        var minMaxResults = new List<(long? Min, long? Max)>();

        // V4 tables with Timestamp + DataSource
        minMaxResults.Add(
            await GetMinMaxTimestamp(
                context.SensorGlucose.Select(e => (DateTime?)e.Timestamp),
                cancellationToken
            )
        );
        minMaxResults.Add(
            await GetMinMaxTimestamp(
                context.MeterGlucose.Select(e => (DateTime?)e.Timestamp),
                cancellationToken
            )
        );
        minMaxResults.Add(
            await GetMinMaxTimestamp(
                context.Boluses.Select(e => (DateTime?)e.Timestamp),
                cancellationToken
            )
        );
        minMaxResults.Add(
            await GetMinMaxTimestamp(
                context.CarbIntakes.Select(e => (DateTime?)e.Timestamp),
                cancellationToken
            )
        );
        minMaxResults.Add(
            await GetMinMaxTimestamp(
                context.BolusCalculations.Select(e => (DateTime?)e.Timestamp),
                cancellationToken
            )
        );
        minMaxResults.Add(
            await GetMinMaxTimestamp(
                context.Notes.Select(e => (DateTime?)e.Timestamp),
                cancellationToken
            )
        );
        minMaxResults.Add(
            await GetMinMaxTimestamp(
                context.DeviceEvents.Select(e => (DateTime?)e.Timestamp),
                cancellationToken
            )
        );

        // StateSpans uses StartTimestamp
        minMaxResults.Add(
            await GetMinMaxTimestamp(
                context.StateSpans.Select(e => (DateTime?)e.StartTimestamp),
                cancellationToken
            )
        );

        // APS snapshots (V4 replacement for device statuses)
        minMaxResults.Add(
            await GetMinMaxMills(
                context.ApsSnapshots.Select(e =>
                    (long?)new DateTimeOffset(e.Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds()),
                cancellationToken
            )
        );

        // Collect data sources from tables that have DataSource
        var allDataSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (
            var ds in await GetDistinctDataSources(
                context.SensorGlucose.Where(e => e.DataSource != null).Select(e => e.DataSource!),
                cancellationToken
            )
        )
            allDataSources.Add(ds);
        foreach (
            var ds in await GetDistinctDataSources(
                context.MeterGlucose.Where(e => e.DataSource != null).Select(e => e.DataSource!),
                cancellationToken
            )
        )
            allDataSources.Add(ds);
        foreach (
            var ds in await GetDistinctDataSources(
                context.Boluses.Where(e => e.DataSource != null).Select(e => e.DataSource!),
                cancellationToken
            )
        )
            allDataSources.Add(ds);
        foreach (
            var ds in await GetDistinctDataSources(
                context.CarbIntakes.Where(e => e.DataSource != null).Select(e => e.DataSource!),
                cancellationToken
            )
        )
            allDataSources.Add(ds);
        foreach (
            var ds in await GetDistinctDataSources(
                context
                    .BolusCalculations.Where(e => e.DataSource != null)
                    .Select(e => e.DataSource!),
                cancellationToken
            )
        )
            allDataSources.Add(ds);
        foreach (
            var ds in await GetDistinctDataSources(
                context.Notes.Where(e => e.DataSource != null).Select(e => e.DataSource!),
                cancellationToken
            )
        )
            allDataSources.Add(ds);
        foreach (
            var ds in await GetDistinctDataSources(
                context.DeviceEvents.Where(e => e.DataSource != null).Select(e => e.DataSource!),
                cancellationToken
            )
        )
            allDataSources.Add(ds);
        // StateSpans uses Source (not DataSource)
        foreach (
            var ds in await GetDistinctDataSources(
                context.StateSpans.Where(e => e.Source != null).Select(e => e.Source!),
                cancellationToken
            )
        )
            allDataSources.Add(ds);
        // Derive year range from all min/max mills
        long? globalMin = null;
        long? globalMax = null;

        foreach (var (min, max) in minMaxResults)
        {
            if (min.HasValue && (!globalMin.HasValue || min.Value < globalMin.Value))
                globalMin = min.Value;
            if (max.HasValue && (!globalMax.HasValue || max.Value > globalMax.Value))
                globalMax = max.Value;
        }

        var tz = await GetUserTimeZoneAsync(cancellationToken);
        var years = Array.Empty<int>();
        if (globalMin.HasValue && globalMax.HasValue)
        {
            var minLocal = TimeZoneInfo.ConvertTime(
                DateTimeOffset.FromUnixTimeMilliseconds(globalMin.Value),
                tz
            );
            var maxLocal = TimeZoneInfo.ConvertTime(
                DateTimeOffset.FromUnixTimeMilliseconds(globalMax.Value),
                tz
            );
            years = Enumerable.Range(minLocal.Year, maxLocal.Year - minLocal.Year + 1).ToArray();
        }

        return new DataOverviewYearsResponse
        {
            Years = years,
            AvailableDataSources = allDataSources
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
        };
    }

    /// <inheritdoc />
    public async Task<DailySummaryResponse> GetDailySummaryAsync(
        int year,
        string[]? dataSources = null,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Getting daily summary for year {Year}, dataSources={DataSources}",
            year,
            dataSources != null ? string.Join(",", dataSources) : "(all)"
        );

        await using var context = await _factory.CreateAsync(cancellationToken);

        var tz = await GetUserTimeZoneAsync(cancellationToken);
        var localYearStart = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var localNextYearStart = new DateTime(year + 1, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(localYearStart, tz);
        var endUtc = TimeZoneInfo.ConvertTimeToUtc(localNextYearStart, tz);
        var startMills = new DateTimeOffset(startUtc, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var endMills = new DateTimeOffset(endUtc, TimeSpan.Zero).ToUnixTimeMilliseconds();

        var hasFilter = dataSources is { Length: > 0 };

        // Dictionary keyed by date string "yyyy-MM-dd" -> DailySummaryDay
        var dayMap = new Dictionary<string, DailySummaryDay>();

        // Run all queries sequentially — DbContext is not thread-safe

        // Exclude non-primary duplicates from cross-connector deduplication
        var npSensorGlucose = context
            .LinkedRecords.Where(lr => lr.RecordType == "sensorglucose" && !lr.IsPrimary)
            .Select(lr => lr.RecordId);
        var npBolus = context
            .LinkedRecords.Where(lr => lr.RecordType == "bolus" && !lr.IsPrimary)
            .Select(lr => lr.RecordId);
        var npCarbIntake = context
            .LinkedRecords.Where(lr => lr.RecordType == "carbintake" && !lr.IsPrimary)
            .Select(lr => lr.RecordId);
        var npBolusCalc = context
            .LinkedRecords.Where(lr => lr.RecordType == "boluscalculation" && !lr.IsPrimary)
            .Select(lr => lr.RecordId);
        var npNote = context
            .LinkedRecords.Where(lr => lr.RecordType == "note" && !lr.IsPrimary)
            .Select(lr => lr.RecordId);
        var npDeviceEvent = context
            .LinkedRecords.Where(lr => lr.RecordType == "deviceevent" && !lr.IsPrimary)
            .Select(lr => lr.RecordId);
        var npStateSpan = context
            .LinkedRecords.Where(lr => lr.RecordType == "statespan" && !lr.IsPrimary)
            .Select(lr => lr.RecordId);

        // V4 tables with Timestamp + DataSource
        await CollectCountsFromTimestampTable(
            "Glucose",
            context
                .SensorGlucose.Where(e => e.Timestamp >= startUtc && e.Timestamp < endUtc)
                .Where(e => !hasFilter || dataSources!.Contains(e.DataSource!))
                .Where(e => !npSensorGlucose.Contains(e.Id))
                .Select(e => e.Timestamp),
            dayMap,
            tz,
            cancellationToken
        );

        await CollectCountsFromTimestampTable(
            "ManualBG",
            context
                .MeterGlucose.Where(e => e.Timestamp >= startUtc && e.Timestamp < endUtc)
                .Where(e => !hasFilter || dataSources!.Contains(e.DataSource!))
                .Select(e => e.Timestamp),
            dayMap,
            tz,
            cancellationToken
        );

        await CollectCountsFromTimestampTable(
            "Boluses",
            context
                .Boluses.Where(e => e.Timestamp >= startUtc && e.Timestamp < endUtc)
                .Where(e => !hasFilter || dataSources!.Contains(e.DataSource!))
                .Where(e => !npBolus.Contains(e.Id))
                .Select(e => e.Timestamp),
            dayMap,
            tz,
            cancellationToken
        );

        await CollectCountsFromTimestampTable(
            "CarbIntake",
            context
                .CarbIntakes.Where(e => e.Timestamp >= startUtc && e.Timestamp < endUtc)
                .Where(e => !hasFilter || dataSources!.Contains(e.DataSource!))
                .Where(e => !npCarbIntake.Contains(e.Id))
                .Select(e => e.Timestamp),
            dayMap,
            tz,
            cancellationToken
        );

        await CollectCountsFromTimestampTable(
            "BolusCalculations",
            context
                .BolusCalculations.Where(e => e.Timestamp >= startUtc && e.Timestamp < endUtc)
                .Where(e => !hasFilter || dataSources!.Contains(e.DataSource!))
                .Where(e => !npBolusCalc.Contains(e.Id))
                .Select(e => e.Timestamp),
            dayMap,
            tz,
            cancellationToken
        );

        await CollectCountsFromTimestampTable(
            "Notes",
            context
                .Notes.Where(e => e.Timestamp >= startUtc && e.Timestamp < endUtc)
                .Where(e => !hasFilter || dataSources!.Contains(e.DataSource!))
                .Where(e => !npNote.Contains(e.Id))
                .Select(e => e.Timestamp),
            dayMap,
            tz,
            cancellationToken
        );

        await CollectCountsFromTimestampTable(
            "DeviceEvents",
            context
                .DeviceEvents.Where(e => e.Timestamp >= startUtc && e.Timestamp < endUtc)
                .Where(e => !hasFilter || dataSources!.Contains(e.DataSource!))
                .Where(e => !npDeviceEvent.Contains(e.Id))
                .Select(e => e.Timestamp),
            dayMap,
            tz,
            cancellationToken
        );

        // StateSpans: uses StartTimestamp and Source (not Timestamp/DataSource)
        await CollectCountsFromTimestampTable(
            "StateSpans",
            context
                .StateSpans.Where(e => e.StartTimestamp >= startUtc && e.StartTimestamp < endUtc)
                .Where(e => !hasFilter || dataSources!.Contains(e.Source!))
                .Where(e => !npStateSpan.Contains(e.Id))
                .Select(e => e.StartTimestamp),
            dayMap,
            tz,
            cancellationToken
        );

        // APS snapshots: V4 replacement for device statuses - skip when filter is active
        if (!hasFilter)
        {
            var apsStartUtc = DateTimeOffset.FromUnixTimeMilliseconds(startMills).UtcDateTime;
            var apsEndUtc = DateTimeOffset.FromUnixTimeMilliseconds(endMills).UtcDateTime;
            await CollectCountsFromTimestampTable(
                "DeviceStatus",
                context
                    .ApsSnapshots.Where(e => e.Timestamp >= apsStartUtc && e.Timestamp < apsEndUtc)
                    .Select(e => e.Timestamp),
                dayMap,
                tz,
                cancellationToken
            );
        }

        // Glucose averages (SensorGlucose + MeterGlucose)
        await CollectGlucoseAverages(
            context,
            startUtc,
            endUtc,
            dataSources,
            hasFilter,
            dayMap,
            tz,
            cancellationToken
        );

        // Insulin totals (Bolus from Boluses table + Basal from algorithm boluses & TempBasals)
        await CollectInsulinTotals(
            context,
            startUtc,
            endUtc,
            dataSources,
            hasFilter,
            dayMap,
            tz,
            cancellationToken
        );

        // Carb totals
        await CollectCarbTotals(
            context,
            startUtc,
            endUtc,
            dataSources,
            hasFilter,
            dayMap,
            tz,
            cancellationToken
        );

        // Compute TotalCount and TotalDailyDose for each day
        foreach (var day in dayMap.Values)
        {
            day.TotalCount = day.Counts.Values.Sum();

            if (day.TotalBolusUnits.HasValue || day.TotalBasalUnits.HasValue)
            {
                day.TotalDailyDose = (day.TotalBolusUnits ?? 0) + (day.TotalBasalUnits ?? 0);
            }
        }

        return new DailySummaryResponse
        {
            Year = year,
            DataSources = dataSources,
            Days = dayMap.Values.OrderBy(d => d.Date).ToArray(),
        };
    }

    /// <inheritdoc />
    public async Task<GriTimelineResponse> GetGriTimelineAsync(
        int year,
        string[]? dataSources = null,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Getting GRI timeline for year {Year}, dataSources={DataSources}",
            year,
            dataSources != null ? string.Join(",", dataSources) : "(all)"
        );

        await using var context = await _factory.CreateAsync(cancellationToken);

        var tz = await GetUserTimeZoneAsync(cancellationToken);
        var hasFilter = dataSources is { Length: > 0 };
        var periods = new List<GriTimelinePeriod>();

        // Minimum readings required for a valid GRI calculation (72 = ~6 hours of 5-min CGM data)
        const int minimumReadings = 72;

        // Compute year-level UTC boundaries once
        var localYearStart = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var localNextYearStart = new DateTime(year + 1, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(localYearStart, tz);
        var endUtc = TimeZoneInfo.ConvertTimeToUtc(localNextYearStart, tz);

        // Hoist LinkedRecord subqueries — IQueryable construction is free
        var npSensorGlucoseIds = context
            .LinkedRecords.Where(lr => lr.RecordType == "sensorglucose" && !lr.IsPrimary)
            .Select(lr => lr.RecordId);
        var nonPrimaryBolusIds = context
            .LinkedRecords.Where(lr => lr.RecordType == "bolus" && !lr.IsPrimary)
            .Select(lr => lr.RecordId);
        var nonPrimaryTempBasalIds = context
            .LinkedRecords.Where(lr => lr.RecordType == "tempbasal" && !lr.IsPrimary)
            .Select(lr => lr.RecordId);
        var nonPrimaryCarbIds = context
            .LinkedRecords.Where(lr => lr.RecordType == "carbintake" && !lr.IsPrimary)
            .Select(lr => lr.RecordId);

        // --- Collect glucose readings by month (CGM + meter) ---
        // Each source is queried independently so one failure doesn't prevent the others.
        var allGlucoseByMonth = new Dictionary<int, List<double>>();

        // SensorGlucose (CGM)
        await AccumulateMonthlyReadingsAsync(
            context.SensorGlucose
                .Where(e => e.Timestamp >= startUtc && e.Timestamp < endUtc && e.Mgdl > 0)
                .Where(e => !hasFilter || dataSources!.Contains(e.DataSource!))
                .Where(e => !npSensorGlucoseIds.Contains(e.Id))
                .Select(e => new { e.Timestamp, e.Mgdl }),
            r => r.Timestamp, r => r.Mgdl, allGlucoseByMonth, tz,
            "Failed to collect SensorGlucose for GRI year {Year}", year, cancellationToken);

        // MeterGlucose (finger sticks)
        await AccumulateMonthlyReadingsAsync(
            context.MeterGlucose
                .Where(e => e.Timestamp >= startUtc && e.Timestamp < endUtc && e.Mgdl > 0)
                .Where(e => !hasFilter || dataSources!.Contains(e.DataSource!))
                .Select(e => new { e.Timestamp, e.Mgdl }),
            r => r.Timestamp, r => r.Mgdl, allGlucoseByMonth, tz,
            "Failed to collect MeterGlucose for GRI year {Year}", year, cancellationToken);

        // --- Collect insulin totals by month (manual bolus, algorithm bolus, temp basal) ---
        // Manual boluses
        var manualBolusByMonth = new Dictionary<int, double>();
        await AccumulateMonthlyTotalsAsync(
            context.Boluses
                .Where(e => e.Timestamp >= startUtc && e.Timestamp < endUtc && e.Insulin > 0)
                .Where(e => e.BolusKind != "Algorithm")
                .Where(e => !hasFilter || dataSources!.Contains(e.DataSource!))
                .Where(e => !nonPrimaryBolusIds.Contains(e.Id))
                .Select(e => new { e.Timestamp, e.Insulin }),
            r => r.Timestamp, r => r.Insulin, manualBolusByMonth, tz,
            "Failed to collect manual bolus totals for GRI year {Year}", year, cancellationToken);

        // Algorithm boluses (APS SMBs -> basal)
        var algorithmBolusByMonth = new Dictionary<int, double>();
        await AccumulateMonthlyTotalsAsync(
            context.Boluses
                .Where(e => e.Timestamp >= startUtc && e.Timestamp < endUtc && e.Insulin > 0)
                .Where(e => e.BolusKind == "Algorithm")
                .Where(e => !hasFilter || dataSources!.Contains(e.DataSource!))
                .Where(e => !nonPrimaryBolusIds.Contains(e.Id))
                .Select(e => new { e.Timestamp, e.Insulin }),
            r => r.Timestamp, r => r.Insulin, algorithmBolusByMonth, tz,
            "Failed to collect algorithm bolus totals for GRI year {Year}", year, cancellationToken);

        // TempBasals (pump basal delivery): insulin = rate * duration, defaulting to a 5-minute span.
        var tempBasalByMonth = new Dictionary<int, double>();
        await AccumulateMonthlyTotalsAsync(
            context.TempBasals
                .Where(e => e.StartTimestamp >= startUtc && e.StartTimestamp < endUtc && e.Rate > 0)
                .Where(e => !hasFilter || dataSources!.Contains(e.DataSource!))
                .Where(e => !nonPrimaryTempBasalIds.Contains(e.Id))
                .Select(e => new { e.StartTimestamp, e.Rate, e.EndTimestamp }),
            r => r.StartTimestamp,
            r => r.Rate * (r.EndTimestamp.HasValue
                ? (r.EndTimestamp.Value - r.StartTimestamp).TotalHours
                : 5.0 / 60.0),
            tempBasalByMonth, tz,
            "Failed to collect TempBasal totals for GRI year {Year}", year, cancellationToken);

        // --- Collect carb totals by month ---
        var carbsByMonth = new Dictionary<int, double>();
        await AccumulateMonthlyTotalsAsync(
            context.CarbIntakes
                .Where(e => e.Timestamp >= startUtc && e.Timestamp < endUtc && e.Carbs > 0)
                .Where(e => !hasFilter || dataSources!.Contains(e.DataSource!))
                .Where(e => !nonPrimaryCarbIds.Contains(e.Id))
                .Select(e => new { e.Timestamp, e.Carbs }),
            r => r.Timestamp, r => r.Carbs, carbsByMonth, tz,
            "Failed to collect carb totals for GRI year {Year}", year, cancellationToken);

        // --- Group by month and compute GRI, TDD, carbs per period ---
        for (var month = 1; month <= 12; month++)
        {
            var period = BuildGriPeriod(
                month, year, minimumReadings,
                allGlucoseByMonth, manualBolusByMonth, algorithmBolusByMonth, tempBasalByMonth, carbsByMonth);
            if (period != null)
                periods.Add(period);
        }

        return new GriTimelineResponse { Year = year, Periods = periods.ToArray() };
    }

    /// <summary>
    /// Local month (1-12) for a UTC timestamp, in the user's time zone.
    /// </summary>
    private static int TimestampToMonth(DateTime utcTimestamp, TimeZoneInfo tz)
    {
        var utcDto = new DateTimeOffset(utcTimestamp, TimeSpan.Zero);
        var local = TimeZoneInfo.ConvertTime(utcDto, tz);
        return local.Month;
    }

    /// <summary>
    /// Materializes a timestamped/valued query and appends each reading to its month's bucket.
    /// A query failure is logged and leaves the accumulator untouched (per-source isolation).
    /// </summary>
    private async Task AccumulateMonthlyReadingsAsync<T>(
        IQueryable<T> query,
        Func<T, DateTime> timestampSelector,
        Func<T, double> valueSelector,
        Dictionary<int, List<double>> readingsByMonth,
        TimeZoneInfo tz,
        string failureMessage,
        int year,
        CancellationToken cancellationToken)
    {
        try
        {
            var rows = await query.ToListAsync(cancellationToken);
            foreach (var row in rows)
            {
                var month = TimestampToMonth(timestampSelector(row), tz);
                if (!readingsByMonth.TryGetValue(month, out var list))
                {
                    list = new List<double>();
                    readingsByMonth[month] = list;
                }
                list.Add(valueSelector(row));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, failureMessage, year);
        }
    }

    /// <summary>
    /// Materializes a timestamped/valued query and sums positive values into per-month totals.
    /// Non-positive values are ignored. A query failure is logged and leaves the accumulator untouched.
    /// </summary>
    private async Task AccumulateMonthlyTotalsAsync<T>(
        IQueryable<T> query,
        Func<T, DateTime> timestampSelector,
        Func<T, double> valueSelector,
        Dictionary<int, double> totalsByMonth,
        TimeZoneInfo tz,
        string failureMessage,
        int year,
        CancellationToken cancellationToken)
    {
        try
        {
            var rows = await query.ToListAsync(cancellationToken);
            foreach (var row in rows)
            {
                var value = valueSelector(row);
                if (value <= 0)
                    continue;
                var month = TimestampToMonth(timestampSelector(row), tz);
                totalsByMonth.TryGetValue(month, out var existing);
                totalsByMonth[month] = existing + value;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, failureMessage, year);
        }
    }

    /// <summary>
    /// Builds one month's GRI timeline period, or null when the month has fewer than
    /// <paramref name="minimumReadings"/> glucose readings.
    /// </summary>
    private GriTimelinePeriod? BuildGriPeriod(
        int month,
        int year,
        int minimumReadings,
        Dictionary<int, List<double>> allGlucoseByMonth,
        Dictionary<int, double> manualBolusByMonth,
        Dictionary<int, double> algorithmBolusByMonth,
        Dictionary<int, double> tempBasalByMonth,
        Dictionary<int, double> carbsByMonth)
    {
        if (
            !allGlucoseByMonth.TryGetValue(month, out var glucoseReadings)
            || glucoseReadings.Count < minimumReadings
        )
            return null;

        // Bucket readings into TIR zones
        var totalCount = glucoseReadings.Count;
        var veryLowCount = glucoseReadings.Count(v => v < 54);
        var lowCount = glucoseReadings.Count(v => v >= 54 && v < 70);
        var targetCount = glucoseReadings.Count(v => v >= 70 && v <= 180);
        var highCount = glucoseReadings.Count(v => v > 180 && v <= 250);
        var veryHighCount = glucoseReadings.Count(v => v > 250);

        var percentages = new TimeInRangePercentages
        {
            VeryLow = (double)veryLowCount / totalCount * 100.0,
            Low = (double)lowCount / totalCount * 100.0,
            Target = (double)targetCount / totalCount * 100.0,
            High = (double)highCount / totalCount * 100.0,
            VeryHigh = (double)veryHighCount / totalCount * 100.0,
        };

        var timeInRange = new TimeInRangeMetrics { Percentages = percentages };

        var gri = _statisticsService.CalculateGRI(timeInRange);
        var averageGlucose = Math.Round(glucoseReadings.Average(), 1);

        // Compute TDD for the month
        var localMonthStart = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var localMonthEnd =
            month == 12
                ? new DateTime(year + 1, 1, 1, 0, 0, 0, DateTimeKind.Unspecified)
                : new DateTime(year, month + 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var daysInMonth = (localMonthEnd - localMonthStart).TotalDays;

        double? totalDailyDose = null;
        manualBolusByMonth.TryGetValue(month, out var totalBolusUnits);
        algorithmBolusByMonth.TryGetValue(month, out var algorithmBasalUnits);
        tempBasalByMonth.TryGetValue(month, out var tempBasalUnits);
        var totalBasalUnits = algorithmBasalUnits + tempBasalUnits;

        if (totalBolusUnits > 0 || totalBasalUnits > 0)
        {
            var totalInsulin = totalBolusUnits + totalBasalUnits;
            totalDailyDose = Math.Round(totalInsulin / daysInMonth, 2);
        }

        // Average daily carbs for the month
        double? averageDailyCarbs = null;
        if (carbsByMonth.TryGetValue(month, out var carbSum) && carbSum > 0)
            averageDailyCarbs = Math.Round(carbSum / daysInMonth, 1);

        var periodStartStr = localMonthStart.ToString("yyyy-MM-dd");
        var periodEndStr = localMonthEnd.AddDays(-1).ToString("yyyy-MM-dd");

        return new GriTimelinePeriod
        {
            PeriodStart = periodStartStr,
            PeriodEnd = periodEndStr,
            Gri = gri,
            AverageGlucoseMgdl = averageGlucose,
            TotalDailyDose = totalDailyDose,
            AverageDailyCarbs = averageDailyCarbs,
            ReadingCount = totalCount,
        };
    }

    /// <summary>
    /// Gets min and max from an IQueryable of nullable longs, with exception handling per table.
    /// </summary>
    private async Task<(long? Min, long? Max)> GetMinMaxMills(
        IQueryable<long?> millsQuery,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var min = await millsQuery.MinAsync(cancellationToken);
            var max = await millsQuery.MaxAsync(cancellationToken);
            return (min, max);
        }
        catch (InvalidOperationException)
        {
            // Table is empty - Min/Max on empty sequence
            return (null, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get min/max mills from table");
            return (null, null);
        }
    }

    /// <summary>
    /// Gets min and max from an IQueryable of nullable DateTimes (V4 entities), converting to mills.
    /// </summary>
    private async Task<(long? Min, long? Max)> GetMinMaxTimestamp(
        IQueryable<DateTime?> timestampQuery,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var min = await timestampQuery.MinAsync(cancellationToken);
            var max = await timestampQuery.MaxAsync(cancellationToken);
            return (
                min.HasValue
                    ? new DateTimeOffset(min.Value, TimeSpan.Zero).ToUnixTimeMilliseconds()
                    : null,
                max.HasValue
                    ? new DateTimeOffset(max.Value, TimeSpan.Zero).ToUnixTimeMilliseconds()
                    : null
            );
        }
        catch (InvalidOperationException)
        {
            return (null, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get min/max timestamp from table");
            return (null, null);
        }
    }

    /// <summary>
    /// Gets distinct non-null data source values from a query, with exception handling.
    /// </summary>
    private async Task<List<string>> GetDistinctDataSources(
        IQueryable<string> dataSourceQuery,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return await dataSourceQuery.Distinct().ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get distinct data sources from table");
            return [];
        }
    }

    /// <summary>
    /// Materializes mills values from a table, groups by date in-memory, and merges counts into the dayMap.
    /// </summary>
    private async Task CollectCountsFromMillsTable(
        string dataType,
        IQueryable<long> millsQuery,
        Dictionary<string, DailySummaryDay> dayMap,
        TimeZoneInfo tz,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var millsList = await millsQuery.ToListAsync(cancellationToken);

            var grouped = millsList
                .GroupBy(m => MillsToDateString(m, tz))
                .Select(g => new { Date = g.Key, Count = g.Count() });

            foreach (var group in grouped)
            {
                if (!dayMap.TryGetValue(group.Date, out var day))
                {
                    day = new DailySummaryDay { Date = group.Date };
                    dayMap[group.Date] = day;
                }

                day.Counts.TryGetValue(dataType, out var existing);
                day.Counts[dataType] = existing + group.Count;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect counts for {DataType}", dataType);
        }
    }

    /// <summary>
    /// Collects glucose averages from SensorGlucose and MeterGlucose.
    /// Each source is queried independently so one failure doesn't prevent the others.
    /// </summary>
    private async Task CollectGlucoseAverages(
        NocturneDbContext context,
        DateTime startUtc,
        DateTime endUtc,
        string[]? dataSources,
        bool hasFilter,
        Dictionary<string, DailySummaryDay> dayMap,
        TimeZoneInfo tz,
        CancellationToken cancellationToken
    )
    {
        // Collect readings from multiple sources independently
        var allReadings = new List<(DateTime Timestamp, double Mgdl)>();

        // SensorGlucose (CGM) - V4 entity uses Timestamp
        try
        {
            var npSensorGlucoseIds = context
                .LinkedRecords.Where(lr => lr.RecordType == "sensorglucose" && !lr.IsPrimary)
                .Select(lr => lr.RecordId);

            var sensorReadings = await context
                .SensorGlucose.Where(e =>
                    e.Timestamp >= startUtc && e.Timestamp < endUtc && e.Mgdl > 0
                )
                .Where(e => !hasFilter || dataSources!.Contains(e.DataSource!))
                .Where(e => !npSensorGlucoseIds.Contains(e.Id))
                .Select(e => new { e.Timestamp, e.Mgdl })
                .ToListAsync(cancellationToken);

            allReadings.AddRange(sensorReadings.Select(r => (r.Timestamp, r.Mgdl)));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect glucose averages from SensorGlucose");
        }

        // MeterGlucose (finger sticks) - V4 entity uses Timestamp
        try
        {
            var meterReadings = await context
                .MeterGlucose.Where(e =>
                    e.Timestamp >= startUtc && e.Timestamp < endUtc && e.Mgdl > 0
                )
                .Where(e => !hasFilter || dataSources!.Contains(e.DataSource!))
                .Select(e => new { e.Timestamp, e.Mgdl })
                .ToListAsync(cancellationToken);

            allReadings.AddRange(meterReadings.Select(r => (r.Timestamp, r.Mgdl)));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect glucose averages from MeterGlucose");
        }

        if (allReadings.Count == 0)
        {
            _logger.LogDebug(
                "No glucose readings found for year range {StartUtc}-{EndUtc}",
                startUtc,
                endUtc
            );
            return;
        }

        // Group by date and compute daily averages + time in range
        var grouped = allReadings
            .GroupBy(r => TimestampToDateString(r.Timestamp, tz))
            .Select(g =>
            {
                var readings = g.ToList();
                var total = readings.Count;
                var inRange = readings.Count(r => r.Mgdl >= 70 && r.Mgdl <= 180);
                return new
                {
                    Date = g.Key,
                    AvgMgdl = readings.Average(r => r.Mgdl),
                    TimeInRangePercent = total > 0 ? Math.Round((double)inRange / total * 100.0, 1) : (double?)null
                };
            });

        foreach (var group in grouped)
        {
            if (!dayMap.TryGetValue(group.Date, out var day))
            {
                day = new DailySummaryDay { Date = group.Date };
                dayMap[group.Date] = day;
            }

            day.AverageGlucoseMgdl = Math.Round(group.AvgMgdl, 1);
            day.TimeInRangePercent = group.TimeInRangePercent;
        }
    }

    /// <summary>
    /// Collects insulin totals from the Boluses table (bolus insulin) and from
    /// algorithm boluses + TempBasals tables (basal insulin delivery).
    /// </summary>
    private async Task CollectInsulinTotals(
        NocturneDbContext context,
        DateTime startUtc,
        DateTime endUtc,
        string[]? dataSources,
        bool hasFilter,
        Dictionary<string, DailySummaryDay> dayMap,
        TimeZoneInfo tz,
        CancellationToken cancellationToken
    )
    {
        // Exclude non-primary duplicates from cross-connector deduplication
        var nonPrimaryBolusIds = context
            .LinkedRecords.Where(lr => lr.RecordType == "bolus" && !lr.IsPrimary)
            .Select(lr => lr.RecordId);

        // Manual bolus records — only user-initiated boluses count as bolus insulin
        try
        {
            var bolusRecords = await context
                .Boluses.Where(e =>
                    e.Timestamp >= startUtc && e.Timestamp < endUtc && e.Insulin > 0
                )
                .Where(e => e.BolusKind != "Algorithm")
                .Where(e => !hasFilter || dataSources!.Contains(e.DataSource!))
                .Where(e => !nonPrimaryBolusIds.Contains(e.Id))
                .Select(e => new { e.Timestamp, e.Insulin })
                .ToListAsync(cancellationToken);

            if (bolusRecords.Count > 0)
            {
                var grouped = bolusRecords
                    .GroupBy(r => TimestampToDateString(r.Timestamp, tz))
                    .Select(g => new { Date = g.Key, BolusUnits = g.Sum(r => r.Insulin) });

                foreach (var group in grouped)
                {
                    if (!dayMap.TryGetValue(group.Date, out var day))
                    {
                        day = new DailySummaryDay { Date = group.Date };
                        dayMap[group.Date] = day;
                    }

                    if (group.BolusUnits > 0)
                        day.TotalBolusUnits = Math.Round(group.BolusUnits, 2);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect bolus insulin totals");
        }

        // Algorithm bolus records (APS-delivered SMBs that contribute to basal insulin)
        try
        {
            var algorithmBolusRecords = await context
                .Boluses.Where(e =>
                    e.Timestamp >= startUtc && e.Timestamp < endUtc && e.Insulin > 0
                )
                .Where(e => e.BolusKind == "Algorithm")
                .Where(e => !hasFilter || dataSources!.Contains(e.DataSource!))
                .Where(e => !nonPrimaryBolusIds.Contains(e.Id))
                .Select(e => new { e.Timestamp, e.Insulin })
                .ToListAsync(cancellationToken);

            if (algorithmBolusRecords.Count > 0)
            {
                var grouped = algorithmBolusRecords
                    .GroupBy(r => TimestampToDateString(r.Timestamp, tz))
                    .Select(g => new { Date = g.Key, TotalBasal = g.Sum(r => r.Insulin) });

                foreach (var group in grouped)
                {
                    if (!dayMap.TryGetValue(group.Date, out var day))
                    {
                        day = new DailySummaryDay { Date = group.Date };
                        dayMap[group.Date] = day;
                    }

                    day.TotalBasalUnits = Math.Round(
                        (day.TotalBasalUnits ?? 0) + group.TotalBasal,
                        2
                    );
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect basal insulin from algorithm boluses");
        }

        // TempBasal records (pump basal delivery with rate x duration)
        try
        {
            var nonPrimaryTempBasalIds = context
                .LinkedRecords.Where(lr => lr.RecordType == "tempbasal" && !lr.IsPrimary)
                .Select(lr => lr.RecordId);

            var tempBasalRecords = await context
                .TempBasals.Where(e =>
                    e.StartTimestamp >= startUtc && e.StartTimestamp < endUtc && e.Rate > 0
                )
                .Where(e => !hasFilter || dataSources!.Contains(e.DataSource!))
                .Where(e => !nonPrimaryTempBasalIds.Contains(e.Id))
                .Select(e => new
                {
                    e.StartTimestamp,
                    e.Rate,
                    e.EndTimestamp,
                })
                .ToListAsync(cancellationToken);

            if (tempBasalRecords.Count > 0)
            {
                const double defaultDurationMinutes = 5.0; // 5 minutes

                var grouped = tempBasalRecords
                    .Select(r =>
                    {
                        var durationHours = (
                            r.EndTimestamp.HasValue
                                ? (r.EndTimestamp.Value - r.StartTimestamp).TotalHours
                                : defaultDurationMinutes / 60.0
                        );
                        var insulin = r.Rate * durationHours;
                        return new
                        {
                            Date = TimestampToDateString(r.StartTimestamp, tz),
                            Insulin = insulin,
                        };
                    })
                    .Where(r => r.Insulin > 0)
                    .GroupBy(r => r.Date)
                    .Select(g => new { Date = g.Key, TotalBasal = g.Sum(r => r.Insulin) });

                foreach (var group in grouped)
                {
                    if (!dayMap.TryGetValue(group.Date, out var day))
                    {
                        day = new DailySummaryDay { Date = group.Date };
                        dayMap[group.Date] = day;
                    }

                    day.TotalBasalUnits = Math.Round(
                        (day.TotalBasalUnits ?? 0) + group.TotalBasal,
                        2
                    );
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect basal insulin from TempBasals");
        }
    }

    /// <summary>
    /// Collects total carbs consumed per day from the CarbIntakes table.
    /// </summary>
    private async Task CollectCarbTotals(
        NocturneDbContext context,
        DateTime startUtc,
        DateTime endUtc,
        string[]? dataSources,
        bool hasFilter,
        Dictionary<string, DailySummaryDay> dayMap,
        TimeZoneInfo tz,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var nonPrimaryCarbIds = context
                .LinkedRecords.Where(lr => lr.RecordType == "carbintake" && !lr.IsPrimary)
                .Select(lr => lr.RecordId);

            var carbRecords = await context
                .CarbIntakes.Where(e =>
                    e.Timestamp >= startUtc && e.Timestamp < endUtc && e.Carbs > 0
                )
                .Where(e => !hasFilter || dataSources!.Contains(e.DataSource!))
                .Where(e => !nonPrimaryCarbIds.Contains(e.Id))
                .Select(e => new { e.Timestamp, e.Carbs })
                .ToListAsync(cancellationToken);

            if (carbRecords.Count == 0)
                return;

            var grouped = carbRecords
                .GroupBy(r => TimestampToDateString(r.Timestamp, tz))
                .Select(g => new { Date = g.Key, TotalCarbs = g.Sum(r => r.Carbs) });

            foreach (var group in grouped)
            {
                if (!dayMap.TryGetValue(group.Date, out var day))
                {
                    day = new DailySummaryDay { Date = group.Date };
                    dayMap[group.Date] = day;
                }

                day.TotalCarbs = Math.Round(group.TotalCarbs, 1);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect carb totals");
        }
    }

    /// <summary>
    /// Converts Unix milliseconds to a local date string in "yyyy-MM-dd" format using the given timezone.
    /// </summary>
    private static string MillsToDateString(long mills, TimeZoneInfo tz)
    {
        var utc = DateTimeOffset.FromUnixTimeMilliseconds(mills);
        var local = TimeZoneInfo.ConvertTime(utc, tz);
        return local.ToString("yyyy-MM-dd");
    }

    /// <summary>
    /// Converts a UTC DateTime to a local date string in "yyyy-MM-dd" format using the given timezone.
    /// </summary>
    private static string TimestampToDateString(DateTime timestamp, TimeZoneInfo tz)
    {
        var utcDto = new DateTimeOffset(timestamp, TimeSpan.Zero);
        var local = TimeZoneInfo.ConvertTime(utcDto, tz);
        return local.ToString("yyyy-MM-dd");
    }

    /// <summary>
    /// Materializes timestamp values from a V4 table, groups by date in-memory, and merges counts into the dayMap.
    /// </summary>
    private async Task CollectCountsFromTimestampTable(
        string dataType,
        IQueryable<DateTime> timestampQuery,
        Dictionary<string, DailySummaryDay> dayMap,
        TimeZoneInfo tz,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var timestampList = await timestampQuery.ToListAsync(cancellationToken);

            var grouped = timestampList
                .GroupBy(t => TimestampToDateString(t, tz))
                .Select(g => new { Date = g.Key, Count = g.Count() });

            foreach (var group in grouped)
            {
                if (!dayMap.TryGetValue(group.Date, out var day))
                {
                    day = new DailySummaryDay { Date = group.Date };
                    dayMap[group.Date] = day;
                }

                day.Counts.TryGetValue(dataType, out var existing);
                day.Counts[dataType] = existing + group.Count;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect counts for {DataType}", dataType);
        }
    }
}
