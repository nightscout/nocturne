using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Nocturne.API.Helpers;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.ChartData.Stages;

/// <summary>
/// Chart data pipeline stage that computes IOB/COB time series and the basal delivery series.
/// </summary>
/// <remarks>
/// <para>
/// IOB and COB are computed at each interval step across the requested time window.
/// Treatments are kept in time-sorted arrays and the active window is tracked with two-pointer
/// indices that advance with each tick: only boluses within DIA hours of the current timestamp
/// contribute to IOB, and only carb intakes within 6 hours contribute to COB. Total inner-loop
/// work is therefore O(ticks + active-window) rather than O(ticks × treatments).
/// The DIA value is read from the loaded profile.
/// </para>
/// <para>
/// Results are cached in <see cref="Microsoft.Extensions.Caching.Memory.IMemoryCache"/> for
/// one minute. The cache key is a 64-bit SHA-256 prefix of the treatment fingerprint (mills,
/// insulin, carbs, temp basal rate) combined with the tenant ID, rounded time boundaries,
/// and interval. The tenant ID component prevents cross-tenant cache leakage.
/// </para>
/// <para>
/// Basal series construction (<see cref="BuildBasalSeriesFromTempBasalsAsync"/>) uses v4
/// <see cref="TempBasal"/> records as the source of truth and fills any gaps with
/// profile-inferred rates at 5-minute resolution. When no TempBasal records exist the
/// entire series is inferred from the profile. The y-axis maximum is clamped to at least
/// 2.5× the default basal rate so the chart always shows meaningful scale.
/// </para>
/// <para>
/// The global IOB minimum is clamped to 3 U and COB minimum to 30 g so the chart axes
/// are never collapsed to near-zero.
/// </para>
/// </remarks>
/// <seealso cref="IChartDataStage"/>
/// <seealso cref="ChartDataContext"/>
internal sealed class IobCobComputeStage(
    IIobCalculator iobCalculator,
    ICobCalculator cobCalculator,
    ITherapySettingsResolver therapySettingsResolver,
    IBasalRateResolver basalRateResolver,
    ITherapyTimelineResolver therapyTimelineResolver,
    IMemoryCache cache,
    ITenantAccessor tenantAccessor,
    ILogger<IobCobComputeStage> logger
) : IChartDataStage
{
    private static readonly TimeSpan IobCobCacheExpiration = TimeSpan.FromMinutes(1);

    private string TenantCacheId => tenantAccessor.Context?.TenantId.ToString()
        ?? throw new InvalidOperationException("Tenant context is not resolved");

    public async Task<ChartDataContext> ExecuteAsync(ChartDataContext context, CancellationToken cancellationToken)
    {
        var bolusList = context.BolusList.ToList();
        var carbIntakeList = context.CarbIntakeList.ToList();
        var tempBasalList = context.TempBasalList.ToList();
        var startTime = context.StartTime;
        var endTime = context.EndTime;
        var intervalMinutes = context.IntervalMinutes;
        var defaultBasalRate = context.DefaultBasalRate;

        var (iobSeries, cobSeries, maxIob, maxCob) = await BuildIobCobSeriesAsync(
            bolusList,
            carbIntakeList,
            startTime,
            endTime,
            intervalMinutes,
            tempBasalList,
            cancellationToken
        );

        var basalSeries = await BuildBasalSeriesFromTempBasalsAsync(tempBasalList, startTime, endTime, defaultBasalRate, cancellationToken);

        var maxBasalRate = Math.Max(
            defaultBasalRate * 2.5,
            basalSeries.Any() ? basalSeries.Max(b => b.Rate) : defaultBasalRate
        );

        return context with
        {
            IobSeries = iobSeries,
            CobSeries = cobSeries,
            MaxIob = Math.Max(3, maxIob),
            MaxCob = Math.Max(30, maxCob),
            BasalSeries = basalSeries,
            MaxBasalRate = maxBasalRate,
        };
    }

    internal async Task<(
        List<TimeSeriesPoint> iobSeries,
        List<TimeSeriesPoint> cobSeries,
        double maxIob,
        double maxCob
    )> BuildIobCobSeriesAsync(
        List<Bolus> boluses,
        List<CarbIntake> carbIntakes,
        long startTime,
        long endTime,
        int intervalMinutes,
        List<TempBasal>? tempBasals = null,
        CancellationToken ct = default
    )
    {
        // Generate cache key based on data hash and time range
        var cacheKey = GenerateIobCobCacheKey(boluses, carbIntakes, startTime, endTime, intervalMinutes, tempBasals);

        // Try to get from cache
        if (
            cache.TryGetValue(
                cacheKey,
                out (
                    List<TimeSeriesPoint> iob,
                    List<TimeSeriesPoint> cob,
                    double maxIob,
                    double maxCob
                ) cached
            )
        )
        {
            logger.LogDebug("IOB/COB cache hit for range {Start}-{End}", startTime, endTime);
            return cached;
        }

        logger.LogDebug(
            "IOB/COB cache miss, computing for range {Start}-{End}",
            startTime,
            endTime
        );

        var iobSeries = new List<TimeSeriesPoint>();
        var cobSeries = new List<TimeSeriesPoint>();
        var intervalMs = intervalMinutes * 60 * 1000;
        double maxIob = 0,
            maxCob = 0;

        // Build the request-scoped therapy timeline once. SnapshotAt(t) inside the loop
        // resolves DIA / sensitivity / carb ratio / basal rate / carbsPerHour via in-memory
        // schedule lookup with a sticky cursor — replacing nested async resolver awaits per tick.
        // The window extends one millisecond past endTime so SnapshotAt(endTime) lands inside a segment.
        var timeline = await therapyTimelineResolver.BuildAsync(startTime, endTime + 1, ct: ct);

        // DIA at endTime drives the IOB / temp-basal eviction window. Matches legacy behavior.
        var diaMs = (long)(timeline.SnapshotAt(endTime).Dia * 60 * 60 * 1000);
        var cobAbsorptionMs = 6L * 60 * 60 * 1000;

        // Sort once by Mills/StartMills so the active window can be tracked with two pointers
        // as t advances. The hi index admits entries whose source time is <= t; the lo index
        // evicts entries that have aged past their respective windows (DIA for IOB / temp basal,
        // 6h for COB). Total work across the full tick loop is O(ticks + treatments) instead of
        // O(ticks × treatments) — see remarks on the class.
        var sortedBoluses = boluses
            .Where(b => b.Insulin > 0)
            .OrderBy(b => b.Mills)
            .ToList();
        var sortedCarbs = carbIntakes
            .Where(c => c.Carbs > 0)
            .OrderBy(c => c.Mills)
            .ToList();
        var sortedTempBasals = tempBasals?.OrderBy(tb => tb.StartMills).ToList();

        int insulinHi = 0,
            insulinLo = 0;
        int carbHi = 0,
            carbLo = 0;
        int basalHi = 0,
            basalLo = 0;

        for (long t = startTime; t <= endTime; t += intervalMs)
        {
            ct.ThrowIfCancellationRequested();

            // Admit newly-elapsed entries (Mills/StartMills <= t)
            while (insulinHi < sortedBoluses.Count && sortedBoluses[insulinHi].Mills <= t)
                insulinHi++;
            while (carbHi < sortedCarbs.Count && sortedCarbs[carbHi].Mills <= t)
                carbHi++;
            if (sortedTempBasals is not null)
            {
                while (basalHi < sortedTempBasals.Count && sortedTempBasals[basalHi].StartMills <= t)
                    basalHi++;
            }

            // Evict entries that have aged out of their window
            while (insulinLo < insulinHi && sortedBoluses[insulinLo].Mills < t - diaMs)
                insulinLo++;
            while (carbLo < carbHi && sortedCarbs[carbLo].Mills < t - cobAbsorptionMs)
                carbLo++;
            if (sortedTempBasals is not null)
            {
                while (basalLo < basalHi && sortedTempBasals[basalLo].StartMills < t - diaMs)
                    basalLo++;
            }

            var insulinCount = insulinHi - insulinLo;
            var iobResult = insulinCount > 0
                ? iobCalculator.FromBoluses(sortedBoluses.GetRange(insulinLo, insulinCount), t)
                : new IobResult { Iob = 0 };

            var basalIob = 0.0;
            var basalCount = basalHi - basalLo;
            if (sortedTempBasals is not null && basalCount > 0)
            {
                var basalResult = iobCalculator.FromTempBasals(
                    sortedTempBasals.GetRange(basalLo, basalCount),
                    t
                );
                basalIob = basalResult.BasalIob ?? 0;
            }

            var iob = iobResult.Iob + basalIob;
            iobSeries.Add(new TimeSeriesPoint { Timestamp = t, Value = iob });
            if (iob > maxIob)
                maxIob = iob;

            var carbCount = carbHi - carbLo;
            var cobResult = carbCount > 0
                ? cobCalculator.FromCarbIntakes(
                    sortedCarbs.GetRange(carbLo, carbCount),
                    sortedBoluses.GetRange(insulinLo, insulinCount),
                    sortedTempBasals is not null && basalCount > 0
                        ? sortedTempBasals.GetRange(basalLo, basalCount)
                        : null,
                    t
                )
                : new CobResult { Cob = 0 };

            var cob = cobResult.Cob;
            cobSeries.Add(new TimeSeriesPoint { Timestamp = t, Value = cob });
            if (cob > maxCob)
                maxCob = cob;
        }

        // Cache the result
        var result = (iobSeries, cobSeries, maxIob, maxCob);
        cache.Set(cacheKey, result, IobCobCacheExpiration);

        return result;
    }

    /// <summary>
    /// Generate a cache key for IOB/COB calculations based on data fingerprint and time range.
    /// Uses SHA256 of individual bolus/carb intake mills and values for collision resistance.
    /// Includes tenant ID to prevent cross-tenant cache leakage.
    /// </summary>
    private string GenerateIobCobCacheKey(
        List<Bolus> boluses,
        List<CarbIntake> carbIntakes,
        long startTime,
        long endTime,
        int intervalMinutes,
        List<TempBasal>? tempBasals = null
    )
    {
        // Round start/end times to interval boundaries for better cache hits
        var intervalMs = intervalMinutes * 60 * 1000;
        var roundedStart = (startTime / intervalMs) * intervalMs;
        var roundedEnd = (endTime / intervalMs) * intervalMs;

        // Hash individual data for a collision-resistant fingerprint
        var sb = new StringBuilder();
        foreach (var b in boluses.Where(b => b.Insulin > 0))
        {
            sb.Append(b.Mills).Append(':').Append(b.Insulin).Append('|');
        }
        foreach (var c in carbIntakes.Where(c => c.Carbs > 0))
        {
            sb.Append(c.Mills).Append(':').Append(c.Carbs).Append('|');
        }

        // Include temp basal data in cache key
        if (tempBasals != null)
        {
            foreach (var tb in tempBasals)
            {
                sb.Append(tb.StartMills)
                    .Append(':')
                    .Append(tb.Rate)
                    .Append(':')
                    .Append(tb.EndMills ?? 0)
                    .Append('|');
            }
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[
            ..16
        ]; // First 16 hex chars (64 bits) is sufficient

        return $"iobcob:{TenantCacheId}:{hash}:{roundedStart}:{roundedEnd}:{intervalMinutes}";
    }

    /// <summary>
    /// Build basal series from TempBasal records.
    /// TempBasal records are the v4 source of truth for pump-confirmed basal delivery.
    /// Falls back to profile-based rates when there are gaps in TempBasal data.
    /// </summary>
    internal async Task<List<BasalPoint>> BuildBasalSeriesFromTempBasalsAsync(
        List<TempBasal> tempBasals,
        long startTime,
        long endTime,
        double defaultBasalRate,
        CancellationToken ct = default
    )
    {
        var series = new List<BasalPoint>();
        var sorted = tempBasals.OrderBy(tb => tb.StartMills).ToList();

        logger.LogDebug(
            "Building basal series from {Count} TempBasal records",
            sorted.Count
        );

        if (sorted.Count == 0)
            return await BuildBasalSeriesFromProfileAsync(startTime, endTime, defaultBasalRate, ct);

        long currentTime = startTime;

        var hasData = await therapySettingsResolver.HasDataAsync(ct);

        foreach (var tb in sorted)
        {
            var tbStart = tb.StartMills;
            var tbEnd = tb.EndMills ?? endTime;

            if (tbEnd < startTime || tbStart > endTime)
                continue;

            tbStart = Math.Max(tbStart, startTime);
            tbEnd = Math.Min(tbEnd, endTime);

            if (tbStart > currentTime)
            {
                series.AddRange(
                    await BuildBasalSeriesFromProfileAsync(currentTime, tbStart, defaultBasalRate, ct)
                );
            }

            var origin = MapTempBasalOrigin(tb.Origin);

            var scheduledRate = tb.ScheduledRate
                ?? (hasData
                    ? await basalRateResolver.GetBasalRateAsync(tbStart, ct: ct)
                    : defaultBasalRate);

            series.Add(
                new BasalPoint
                {
                    Timestamp = tbStart,
                    Rate = origin == BasalDeliveryOrigin.Suspended ? 0 : tb.Rate,
                    ScheduledRate = scheduledRate,
                    Origin = origin,
                    FillColor = ChartColorMapper.FillFromBasalOrigin(origin),
                    StrokeColor = ChartColorMapper.StrokeFromBasalOrigin(origin),
                }
            );

            currentTime = tbEnd;
        }

        if (currentTime < endTime)
            series.AddRange(await BuildBasalSeriesFromProfileAsync(currentTime, endTime, defaultBasalRate, ct));

        if (series.Count == 0)
        {
            series.Add(
                new BasalPoint
                {
                    Timestamp = startTime,
                    Rate = defaultBasalRate,
                    ScheduledRate = defaultBasalRate,
                    Origin = BasalDeliveryOrigin.Scheduled,
                    FillColor = ChartColorMapper.FillFromBasalOrigin(BasalDeliveryOrigin.Scheduled),
                    StrokeColor = ChartColorMapper.StrokeFromBasalOrigin(
                        BasalDeliveryOrigin.Scheduled
                    ),
                }
            );
        }

        return series;
    }

    internal async Task<List<BasalPoint>> BuildBasalSeriesFromProfileAsync(
        long startTime,
        long endTime,
        double defaultBasalRate,
        CancellationToken ct = default
    )
    {
        var series = new List<BasalPoint>();
        const long intervalMs = 5 * 60 * 1000;
        double? prevRate = null;

        var hasData = await therapySettingsResolver.HasDataAsync(ct);

        for (long t = startTime; t <= endTime; t += intervalMs)
        {
            var rate = hasData
                ? await basalRateResolver.GetBasalRateAsync(t, ct: ct)
                : defaultBasalRate;

            if (prevRate == null || Math.Abs(rate - prevRate.Value) > 0.001)
            {
                series.Add(
                    new BasalPoint
                    {
                        Timestamp = t,
                        Rate = rate,
                        ScheduledRate = rate,
                        Origin = BasalDeliveryOrigin.Inferred,
                        FillColor = ChartColorMapper.FillFromBasalOrigin(
                            BasalDeliveryOrigin.Inferred
                        ),
                        StrokeColor = ChartColorMapper.StrokeFromBasalOrigin(
                            BasalDeliveryOrigin.Inferred
                        ),
                    }
                );
                prevRate = rate;
            }
        }

        if (series.Count == 0)
        {
            series.Add(
                new BasalPoint
                {
                    Timestamp = startTime,
                    Rate = defaultBasalRate,
                    ScheduledRate = defaultBasalRate,
                    Origin = BasalDeliveryOrigin.Inferred,
                    FillColor = ChartColorMapper.FillFromBasalOrigin(BasalDeliveryOrigin.Inferred),
                    StrokeColor = ChartColorMapper.StrokeFromBasalOrigin(
                        BasalDeliveryOrigin.Inferred
                    ),
                }
            );
        }

        return series;
    }

    /// <summary>
    /// Maps a TempBasalOrigin enum value to the corresponding BasalDeliveryOrigin enum value.
    /// Both enums have identical members (Algorithm, Scheduled, Manual, Suspended, Inferred).
    /// </summary>
    internal static BasalDeliveryOrigin MapTempBasalOrigin(TempBasalOrigin origin) =>
        origin switch
        {
            TempBasalOrigin.Algorithm => BasalDeliveryOrigin.Algorithm,
            TempBasalOrigin.Scheduled => BasalDeliveryOrigin.Scheduled,
            TempBasalOrigin.Manual => BasalDeliveryOrigin.Manual,
            TempBasalOrigin.Suspended => BasalDeliveryOrigin.Suspended,
            TempBasalOrigin.Inferred => BasalDeliveryOrigin.Inferred,
            _ => BasalDeliveryOrigin.Scheduled,
        };
}
