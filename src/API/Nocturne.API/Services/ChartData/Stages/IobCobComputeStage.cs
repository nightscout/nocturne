using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Clinical.Treatments;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.API.Services.Caching;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.ChartData.Stages;

/// <summary>
/// Chart data pipeline stage that computes IOB/COB time series and the basal delivery series.
/// </summary>
/// <remarks>
/// <para>
/// The series itself, and its preference for device-reported values, is <see cref="IobCobSeries"/>.
/// </para>
/// <para>
/// Results are cached in the local tier of <see cref="ITenantCache"/> for one minute. The cache
/// key is a 64-bit SHA-256 prefix of every input the series depends on (treatments, temp basals,
/// APS snapshots and the contents of every therapy segment) combined with the rounded time
/// boundaries and interval.
/// </para>
/// <para>
/// Basal series construction is delegated to <see cref="IBasalSeriesBuilder"/>, which uses
/// v4 <see cref="TempBasal"/> records as the source of truth and fills any gaps with
/// profile-inferred rates at the schedule's exact boundaries. When no TempBasal records
/// exist the entire series is inferred from the profile. The y-axis maximum is clamped to at least
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
    IBasalSeriesBuilder basalSeriesBuilder,
    ITherapyTimelineResolver therapyTimelineResolver,
    ITenantCache cache,
    ILogger<IobCobComputeStage> logger
) : IChartDataStage
{
    private static readonly TimeSpan IobCobCacheExpiration = TimeSpan.FromMinutes(1);

    public async Task<ChartDataContext> ExecuteAsync(ChartDataContext context, CancellationToken cancellationToken)
    {
        var bolusList = context.BolusList.ToList();
        var carbIntakeList = context.CarbIntakeList.ToList();
        var tempBasalList = context.TempBasalList.ToList();
        var startTime = context.StartTime;
        var endTime = context.EndTime;
        var intervalMinutes = context.IntervalMinutes;
        var defaultBasalRate = context.DefaultBasalRate;

        var timeline = await therapyTimelineResolver.BuildAsync(startTime, endTime + 1, ct: cancellationToken);

        var (iobSeries, cobSeries, maxIob, maxCob) = BuildIobCobSeries(
            bolusList, carbIntakeList, startTime, endTime, Math.Min(endTime, context.NowMills), intervalMinutes,
            tempBasalList, timeline, context.ApsSnapshotList, cancellationToken
        );

        var basalSeries = await basalSeriesBuilder.BuildAsync(
            context.DisplayTempBasals.ToList(),
            startTime,
            endTime,
            defaultBasalRate,
            cancellationToken
        );

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

    internal (
        List<TimeSeriesPoint> iobSeries,
        List<TimeSeriesPoint> cobSeries,
        double maxIob,
        double maxCob
    ) BuildIobCobSeries(
        List<Bolus> boluses,
        List<CarbIntake> carbIntakes,
        long startTime,
        long endTime,
        long untilMills,
        int intervalMinutes,
        List<TempBasal>? tempBasals,
        TherapyTimeline timeline,
        IReadOnlyList<ApsIobCobPoint>? apsSnapshots = null,
        CancellationToken ct = default
    )
    {
        var cacheKey = GenerateIobCobCacheKey(boluses, carbIntakes, startTime, endTime, untilMills, intervalMinutes, timeline, tempBasals, apsSnapshots);

        if (
            cache.TryGetLocal(
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

        var ticks = IobCobSeries.Build(
            iobCalculator, cobCalculator, boluses, carbIntakes, tempBasals, timeline, apsSnapshots,
            startTime, endTime, untilMills, intervalMinutes * 60 * 1000L, ct: ct);

        var iobSeries = new List<TimeSeriesPoint>(ticks.Count);
        var cobSeries = new List<TimeSeriesPoint>(ticks.Count);
        double maxIob = 0,
            maxCob = 0;
        foreach (var tick in ticks)
        {
            iobSeries.Add(new TimeSeriesPoint { Timestamp = tick.Mills, Value = tick.Iob });
            cobSeries.Add(new TimeSeriesPoint { Timestamp = tick.Mills, Value = tick.Cob });
            if (tick.Iob > maxIob)
                maxIob = tick.Iob;
            if (tick.Cob > maxCob)
                maxCob = tick.Cob;
        }

        var result = (iobSeries, cobSeries, maxIob, maxCob);
        cache.SetLocal(cacheKey, result, IobCobCacheExpiration);

        return result;
    }

    /// <summary>
    /// Generate a cache key for IOB/COB calculations from a SHA-256 fingerprint of every input the
    /// series depends on and the rounded time range.
    /// </summary>
    /// <remarks>
    /// The therapy part covers each segment's whole snapshot and the boundaries between segments,
    /// not the values at the ticks: the calculation also reads the snapshot between ticks, e.g. DIA
    /// at the window end and the scheduled basal rate at a temp basal's start.
    /// </remarks>
    private static string GenerateIobCobCacheKey(
        List<Bolus> boluses,
        List<CarbIntake> carbIntakes,
        long startTime,
        long endTime,
        long untilMills,
        int intervalMinutes,
        TherapyTimeline timeline,
        List<TempBasal>? tempBasals = null,
        IReadOnlyList<ApsIobCobPoint>? apsSnapshots = null
    )
    {
        // Round start/end times to interval boundaries for better cache hits
        var intervalMs = intervalMinutes * 60 * 1000;
        var roundedStart = (startTime / intervalMs) * intervalMs;
        var roundedEnd = (endTime / intervalMs) * intervalMs;
        var roundedUntil = (untilMills / intervalMs) * intervalMs;

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

        // Include APS snapshot data in cache key — a new snapshot changes the series. Null and 0
        // fingerprint differently ("~" vs "0"): null falls back to computed while 0 is a device
        // value, and sync-id upserts can flip one to the other without changing the timestamp.
        if (apsSnapshots != null)
        {
            foreach (var s in apsSnapshots)
            {
                sb.Append(s.Timestamp.Ticks)
                    .Append(':')
                    .Append(s.Iob?.ToString() ?? "~")
                    .Append(':')
                    .Append(s.Cob?.ToString() ?? "~")
                    .Append('|');
            }
        }

        var segments = timeline.Segments;
        for (var i = 0; i < segments.Count; i++)
        {
            if (i > 0)
                sb.Append('@').Append(segments[i].StartMills).Append(':');
            segments[i].Snapshot.AppendFingerprint(sb);
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[
            ..16
        ]; // First 16 hex chars (64 bits) is sufficient

        return $"iobcob:{hash}:{roundedStart}:{roundedEnd}:{roundedUntil}:{intervalMinutes}";
    }

}
