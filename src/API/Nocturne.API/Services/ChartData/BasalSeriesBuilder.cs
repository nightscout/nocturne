using Microsoft.Extensions.Logging;
using Nocturne.API.Helpers;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.ChartData;

/// <summary>
/// Builds basal delivery series from TempBasal records and profile-inferred rates.
/// TempBasal records are the v4 source of truth for pump-confirmed basal delivery.
/// Gaps in TempBasal coverage are filled at 5-minute resolution from the active
/// basal rate schedule; when no TempBasal records exist the entire series is
/// profile-derived.
/// </summary>
internal sealed class BasalSeriesBuilder(
    ITherapySettingsResolver therapySettingsResolver,
    ITherapyTimelineResolver therapyTimelineResolver,
    ILogger<BasalSeriesBuilder> logger
) : IBasalSeriesBuilder
{
    public async Task<List<BasalPoint>> BuildAsync(
        List<TempBasal> tempBasals,
        long startTime,
        long endTime,
        double defaultBasalRate,
        CancellationToken ct = default
    )
    {
        // Build once — reused by both gap-fill and profile-only paths to avoid per-tick resolver round-trips.
        var timeline = await therapyTimelineResolver.BuildAsync(startTime, endTime + 1, ct: ct);
        var hasData = await therapySettingsResolver.HasDataAsync(ct);

        var series = new List<BasalPoint>();
        var sorted = tempBasals.OrderBy(tb => tb.StartMills).ToList();

        logger.LogDebug("Building basal series from {Count} TempBasal records", sorted.Count);

        if (sorted.Count == 0)
            return BuildFromProfile(startTime, endTime, defaultBasalRate, timeline, hasData);

        long currentTime = startTime;

        foreach (var tb in sorted)
        {
            var tbStart = tb.StartMills;
            var tbEnd = tb.EndMills ?? endTime;

            if (tbEnd < startTime || tbStart > endTime)
                continue;

            tbStart = Math.Max(tbStart, startTime);
            tbEnd = Math.Min(tbEnd, endTime);

            if (tbStart > currentTime)
                series.AddRange(BuildFromProfile(currentTime, tbStart, defaultBasalRate, timeline, hasData));

            var origin = MapTempBasalOrigin(tb.Origin);

            var scheduledRate = tb.ScheduledRate
                ?? (hasData
                    ? timeline.SnapshotAt(tbStart).BasalRateAt(tbStart)
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
            series.AddRange(BuildFromProfile(currentTime, endTime, defaultBasalRate, timeline, hasData));

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
                    StrokeColor = ChartColorMapper.StrokeFromBasalOrigin(BasalDeliveryOrigin.Scheduled),
                }
            );
        }

        return series;
    }

    internal static List<BasalPoint> BuildFromProfile(
        long startTime,
        long endTime,
        double defaultBasalRate,
        TherapyTimeline timeline,
        bool hasData
    )
    {
        var series = new List<BasalPoint>();
        const long intervalMs = 5 * 60 * 1000;
        double? prevRate = null;

        for (long t = startTime; t <= endTime; t += intervalMs)
        {
            var rate = hasData
                ? timeline.SnapshotAt(t).BasalRateAt(t)
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
                        FillColor = ChartColorMapper.FillFromBasalOrigin(BasalDeliveryOrigin.Inferred),
                        StrokeColor = ChartColorMapper.StrokeFromBasalOrigin(BasalDeliveryOrigin.Inferred),
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
                    StrokeColor = ChartColorMapper.StrokeFromBasalOrigin(BasalDeliveryOrigin.Inferred),
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
