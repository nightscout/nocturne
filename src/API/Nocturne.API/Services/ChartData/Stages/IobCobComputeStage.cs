using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Nocturne.API.Helpers;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.ChartData.Stages;

/// <summary>
/// Chart data pipeline stage that computes IOB/COB time series and the basal delivery series.
/// </summary>
/// <remarks>
/// <para>
/// IOB and COB are computed at each interval step across the requested time window.
/// To avoid O(n²) work on wide windows, treatments are pre-filtered before each iteration:
/// only boluses within DIA hours of the current timestamp contribute to IOB, and only
/// carb intakes within 6 hours contribute to COB. The DIA value is read from the loaded profile.
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
    IIobService iobService,
    ICobService cobService,
    ITherapySettingsResolver therapySettingsResolver,
    IBasalRateResolver basalRateResolver,
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
        var syntheticTreatments = context.SyntheticTreatments.ToList();
        var tempBasalList = context.TempBasalList.ToList();
        var startTime = context.StartTime;
        var endTime = context.EndTime;
        var intervalMinutes = context.IntervalMinutes;
        var defaultBasalRate = context.DefaultBasalRate;

        var (iobSeries, cobSeries, maxIob, maxCob) = await BuildIobCobSeriesAsync(
            syntheticTreatments,
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
        List<Treatment> treatments,
        long startTime,
        long endTime,
        int intervalMinutes,
        List<TempBasal>? tempBasals = null,
        CancellationToken ct = default
    )
    {
        // Generate cache key based on treatment data hash and time range
        var cacheKey = GenerateIobCobCacheKey(treatments, startTime, endTime, intervalMinutes, tempBasals);

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

        // Pre-compute DIA and COB absorption window for filtering
        var hasData = await therapySettingsResolver.HasDataAsync(ct);
        var dia = hasData ? await therapySettingsResolver.GetDIAAsync(endTime, ct: ct) : 3.0;
        var diaMs = (long)(dia * 60 * 60 * 1000); // DIA in milliseconds
        var cobAbsorptionMs = 6L * 60 * 60 * 1000; // 6 hours for COB absorption

        // Pre-filter treatments with insulin for IOB calculations
        var insulinTreatments = treatments
            .Where(t => t.Insulin.HasValue && t.Insulin.Value > 0)
            .ToList();

        // Pre-filter treatments with carbs for COB calculations
        var carbTreatments = treatments.Where(t => t.Carbs.HasValue && t.Carbs.Value > 0).ToList();

        for (long t = startTime; t <= endTime; t += intervalMs)
        {
            // Filter to only treatments that could still have active IOB at time t
            // A treatment can only contribute IOB if it was given within DIA hours before t
            var relevantIobTreatments = insulinTreatments
                .Where(tr => tr.Mills <= t && tr.Mills >= t - diaMs)
                .ToList();

            var iobResult =
                relevantIobTreatments.Count > 0
                    ? iobService.FromTreatments(relevantIobTreatments, t, null)
                    : new IobResult { Iob = 0 };

            // Calculate basal IOB from V4 TempBasal records
            var basalIob = 0.0;
            if (tempBasals?.Count > 0)
            {
                var relevantTempBasals = tempBasals
                    .Where(tb => tb.StartMills <= t && tb.StartMills >= t - diaMs)
                    .ToList();

                if (relevantTempBasals.Count > 0)
                {
                    var basalResult = iobService.FromTempBasals(relevantTempBasals, t, null);
                    basalIob = basalResult.BasalIob ?? 0;
                }
            }

            var iob = iobResult.Iob + basalIob;
            iobSeries.Add(new TimeSeriesPoint { Timestamp = t, Value = iob });
            if (iob > maxIob)
                maxIob = iob;

            // Filter to only treatments that could still have active COB at time t
            var relevantCobTreatments = carbTreatments
                .Where(tr => tr.Mills <= t && tr.Mills >= t - cobAbsorptionMs)
                .ToList();

            var cobResult =
                relevantCobTreatments.Count > 0
                    ? await cobService.CobTotalAsync(relevantCobTreatments, t, null, ct)
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
    /// Generate a cache key for IOB/COB calculations based on treatment fingerprint and time range.
    /// Uses SHA256 of individual treatment mills/insulin/carbs values for collision resistance.
    /// Includes tenant ID to prevent cross-tenant cache leakage.
    /// </summary>
    private string GenerateIobCobCacheKey(
        List<Treatment> treatments,
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

        // Hash individual treatment data for a collision-resistant fingerprint
        var sb = new StringBuilder();
        foreach (var t in treatments)
        {
            if (
                (t.Insulin.HasValue && t.Insulin.Value > 0)
                || (t.Carbs.HasValue && t.Carbs.Value > 0)
            )
            {
                sb.Append(t.Mills)
                    .Append(':')
                    .Append(t.Insulin ?? 0)
                    .Append(':')
                    .Append(t.Carbs ?? 0)
                    .Append('|');
            }
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
