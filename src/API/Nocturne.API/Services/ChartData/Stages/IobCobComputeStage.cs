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
/// APS snapshots, overrides and the contents of every therapy segment) combined with the rounded time
/// boundaries and interval.
/// </para>
/// <para>
/// Basal series construction is delegated to <see cref="IBasalSeriesBuilder"/>, which uses
/// v4 <see cref="TempBasal"/> records as the source of truth and fills any gaps with
/// <see cref="ScheduledBasal"/> at its exact boundaries. When no TempBasal records
/// exist the entire series is inferred from it. The y-axis maximum is clamped to at least
/// 2.5× the default basal rate so the chart always shows meaningful scale.
/// </para>
/// <para>
/// The IOB axis maximum is at least 3 U and the COB maximum at least 30 g, so the chart axes are
/// never collapsed to near-zero. The IOB axis minimum is the lowest IOB, and never above 0.
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

        var timeline = await therapyTimelineResolver.BuildAsync(
            context.BufferStartTime, endTime + 1, ct: cancellationToken);

        var (iobSeries, cobSeries, minIob, maxIob, maxCob) = BuildIobCobSeries(
            bolusList, carbIntakeList, startTime, endTime, context.NowMills, intervalMinutes,
            tempBasalList, timeline, context.Overrides, context.ApsSnapshotList, context.PumpIobPointList, cancellationToken
        );

        var basalSeries = await basalSeriesBuilder.BuildAsync(
            context.DisplayTempBasals.ToList(),
            startTime,
            endTime,
            defaultBasalRate,
            context.Overrides,
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
            MinIob = minIob,
            MaxIob = Math.Max(3, maxIob),
            MaxCob = Math.Max(30, maxCob),
            BasalSeries = basalSeries,
            MaxBasalRate = maxBasalRate,
        };
    }

    internal (
        List<TimeSeriesPoint> iobSeries,
        List<TimeSeriesPoint> cobSeries,
        double minIob,
        double maxIob,
        double maxCob
    ) BuildIobCobSeries(
        List<Bolus> boluses,
        List<CarbIntake> carbIntakes,
        long startTime,
        long endTime,
        long nowMills,
        int intervalMinutes,
        List<TempBasal>? tempBasals,
        TherapyTimeline timeline,
        IReadOnlyList<ScaleFactorInterval> overrides,
        IReadOnlyList<ApsIobCobPoint>? apsSnapshots = null,
        IReadOnlyList<PumpIobPoint>? pumpIobPoints = null,
        CancellationToken ct = default
    )
    {
        var cacheKey = GenerateIobCobCacheKey(
            boluses, carbIntakes, startTime, endTime, nowMills, intervalMinutes, timeline, overrides, tempBasals, apsSnapshots, pumpIobPoints);

        if (
            cache.TryGetLocal(
                cacheKey,
                out (
                    List<TimeSeriesPoint> iob,
                    List<TimeSeriesPoint> cob,
                    double minIob,
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
            iobCalculator, cobCalculator, boluses, carbIntakes, tempBasals, timeline,
            new ScheduledBasal(timeline.BasalSegments(), overrides),
            DeviceReadings.Of(apsSnapshots, pumpIobPoints),
            startTime, endTime, nowMills, intervalMinutes * 60 * 1000L, ct: ct);

        var iobSeries = new List<TimeSeriesPoint>(ticks.Count);
        var cobSeries = new List<TimeSeriesPoint>(ticks.Count);
        double minIob = 0,
            maxIob = 0,
            maxCob = 0;
        foreach (var tick in ticks)
        {
            iobSeries.Add(new TimeSeriesPoint { Timestamp = tick.Mills, Value = tick.Iob });
            cobSeries.Add(new TimeSeriesPoint { Timestamp = tick.Mills, Value = tick.Cob });
            if (tick.Iob > maxIob)
                maxIob = tick.Iob;
            if (tick.Iob < minIob)
                minIob = tick.Iob;
            if (tick.Cob > maxCob)
                maxCob = tick.Cob;
        }

        var result = (iobSeries, cobSeries, minIob, maxIob, maxCob);
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
    /// at the window end and the scheduled basal rate under every chunk of a temp basal.
    /// </remarks>
    private static string GenerateIobCobCacheKey(
        List<Bolus> boluses,
        List<CarbIntake> carbIntakes,
        long startTime,
        long endTime,
        long nowMills,
        int intervalMinutes,
        TherapyTimeline timeline,
        IReadOnlyList<ScaleFactorInterval> overrides,
        List<TempBasal>? tempBasals = null,
        IReadOnlyList<ApsIobCobPoint>? apsSnapshots = null,
        IReadOnlyList<PumpIobPoint>? pumpIobPoints = null
    )
    {
        // The ticks run from startTime, not from the epoch, so the key holds the exact start, the last
        // tick, and the tick now falls in: now stops running temps and marks the live tick, and past
        // the last tick's slot it changes nothing.
        long intervalMs = intervalMinutes * 60 * 1000;
        long TickHolding(long mills) =>
            startTime + (long)Math.Floor((mills - startTime) / (double)intervalMs) * intervalMs;
        var lastTick = TickHolding(endTime);
        var nowTick = TickHolding(Math.Min(nowMills, lastTick + intervalMs));

        // Hash individual data for a collision-resistant fingerprint
        var sb = new StringBuilder();
        foreach (var b in boluses.Where(b => b.Insulin > 0))
        {
            sb.Append(b.Mills).Append(':').Append(b.Insulin).Append(':');
            AppendInsulinContext(sb, b.InsulinContext);
            sb.Append('|');
        }
        foreach (var c in carbIntakes.Where(c => c.Carbs > 0))
        {
            sb.Append(c.Mills).Append(':').Append(c.Carbs).Append(':')
                .Append(c.AbsorptionTime?.ToString() ?? "~").Append('|');
        }

        // Temp basals: what each delivers, and what StatisticsService.ClipOverlappingTempBasals
        // reads to choose between streams.
        if (tempBasals != null)
        {
            foreach (var tb in tempBasals)
            {
                sb.Append(tb.StartMills)
                    .Append(':')
                    .Append(tb.Rate?.ToString() ?? "~")
                    .Append(':')
                    .Append(tb.EndMills?.ToString() ?? "~")
                    .Append(':')
                    .Append(tb.Origin)
                    .Append(':')
                    .Append(tb.ScheduledRate?.ToString() ?? "~")
                    .Append(':');
                AppendInsulinContext(sb, tb.InsulinContext);
                sb.Append(':')
                    .Append(tb.DeviceId?.ToString() ?? "~")
                    .Append(':')
                    .Append(tb.Device ?? "~")
                    .Append(':')
                    .Append(tb.OriginEvidence?.ToString() ?? "~")
                    .Append(':')
                    .Append(tb.ApsSnapshotId?.ToString() ?? "~")
                    .Append(':')
                    .Append(tb.App ?? "~")
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
        sb.Append('#');
        foreach (var o in overrides)
            sb.Append(o.Start).Append(':').Append(o.End).Append(':').Append(o.Factor).Append('|');
        sb.Append('#');
        if (pumpIobPoints != null)
        {
            foreach (var p in pumpIobPoints)
            {
                sb.Append(p.Timestamp.Ticks)
                    .Append(':')
                    .Append(p.Iob?.ToString() ?? "~")
                    .Append(':')
                    .Append(p.BolusIob?.ToString() ?? "~")
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

        return $"iobcob:{hash}:{startTime}:{lastTick}:{nowTick}:{intervalMinutes}";
    }

    private static void AppendInsulinContext(StringBuilder sb, TreatmentInsulinContext? context)
    {
        if (context is null)
            sb.Append('~');
        else
            sb.Append(context.Dia).Append('/').Append(context.Peak);
    }

}
