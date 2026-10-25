using Microsoft.Extensions.Logging;
using Nocturne.API.Helpers;
using Nocturne.Core.Clinical.LoopAnalysis;
using Nocturne.Core.Clinical.Treatments;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Basal;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.ChartData;

/// <summary>
/// Builds basal delivery series from TempBasal records and profile-inferred rates.
/// TempBasal records are the v4 source of truth for pump-confirmed basal delivery.
/// Gaps in TempBasal coverage are filled from <see cref="ScheduledBasal"/>, stepping
/// at its exact boundaries; when no TempBasal records exist the entire series is
/// inferred from it. With no therapy settings at all there is no schedule to
/// infer from, so the series is left empty rather than invented: a fabricated
/// flat rate renders as real delivery on the chart.
/// </summary>
internal sealed class BasalSeriesBuilder(
    ITherapySettingsResolver therapySettingsResolver,
    IBasalSegmentService basalSegmentService,
    ILogger<BasalSeriesBuilder> logger
) : IBasalSeriesBuilder
{
    public async Task<List<BasalPoint>> BuildAsync(
        List<TempBasal> tempBasals,
        long startTime,
        long endTime,
        double defaultBasalRate,
        IReadOnlyList<ScaleFactorInterval> overrides,
        CancellationToken ct = default
    )
    {
        var hasData = await therapySettingsResolver.HasDataAsync(ct);

        var series = new List<BasalPoint>();
        var sorted = tempBasals.OrderBy(tb => tb.StartMills).ToList();

        logger.LogDebug("Building basal series from {Count} TempBasal records", sorted.Count);

        var scheduled = new ScheduledBasal(
            hasData ? await basalSegmentService.GetSegmentsAsync(startTime, endTime, ct).ToListAsync(ct) : [],
            overrides);
        var segments = scheduled.Segments([]);
        var cursor = 0;

        if (sorted.Count == 0)
            return FillFromSchedule(segments, ref cursor, startTime, endTime, defaultBasalRate, hasData);

        long currentTime = startTime;

        foreach (var tb in sorted)
        {
            var tbStart = tb.StartMills;
            var tbEnd = tb.EndMills!.Value;

            if (tbEnd < startTime || tbStart > endTime)
                continue;

            tbStart = Math.Max(tbStart, startTime);
            tbEnd = Math.Min(tbEnd, endTime);

            if (tbStart > currentTime)
                series.AddRange(FillFromSchedule(segments, ref cursor, currentTime, tbStart, defaultBasalRate, hasData));

            // A relative temp of unknown rate still cancels the temp before it; the schedule shows
            if (tb.Origin != TempBasalOrigin.Suspended && tb.Rate is null)
            {
                series.AddRange(FillFromSchedule(segments, ref cursor, tbStart, tbEnd, defaultBasalRate, hasData));
                currentTime = tbEnd;
                continue;
            }

            var origin = MapTempBasalOrigin(tb.Origin);

            double? scheduledRate = hasData && segments.Count > 0 ? scheduled.Under(tb, tbStart) : tb.ScheduledRate;

            series.Add(
                new BasalPoint
                {
                    Timestamp = tbStart,
                    Rate = origin == BasalDeliveryOrigin.Suspended ? 0 : tb.Rate!.Value,
                    ScheduledRate = scheduledRate,
                    Origin = origin,
                    FillColor = ChartColorMapper.FillFromBasalOrigin(origin),
                    StrokeColor = ChartColorMapper.StrokeFromBasalOrigin(origin),
                }
            );

            currentTime = tbEnd;
        }

        if (currentTime < endTime)
            series.AddRange(FillFromSchedule(segments, ref cursor, currentTime, endTime, defaultBasalRate, hasData));

        if (series.Count == 0 && hasData)
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

    private static List<BasalPoint> FillFromSchedule(
        IReadOnlyList<RateSegment> segments,
        ref int cursor,
        long startTime,
        long endTime,
        double defaultBasalRate,
        bool hasData
    )
    {
        var series = new List<BasalPoint>();

        // No therapy settings means no basal schedule. Emitting defaultBasalRate
        // here would draw a flat band the wearer never received.
        if (!hasData || startTime >= endTime)
            return series;

        double? prevRate = null;

        if (segments.Count > 0)
        {
            for (
                var i = SegmentIndexAt(segments, ref cursor, startTime);
                i < segments.Count && segments[i].Start < endTime;
                i++
            )
            {
                var segment = segments[i];
                var rate = segment.Rate;

                if (prevRate == null || Math.Abs(rate - prevRate.Value) > 0.001)
                {
                    series.Add(
                        new BasalPoint
                        {
                            Timestamp = Math.Max(startTime, segment.Start),
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
    /// Index of the segment covering <paramref name="mills"/>, or the nearest one when none does.
    /// The cursor only moves as far as the instant needs, so ordered lookups walk the list once.
    /// </summary>
    private static int SegmentIndexAt(IReadOnlyList<RateSegment> segments, ref int cursor, long mills)
    {
        while (cursor > 0 && segments[cursor].Start > mills)
            cursor--;
        while (cursor < segments.Count - 1 && segments[cursor].End <= mills)
            cursor++;
        return cursor;
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
