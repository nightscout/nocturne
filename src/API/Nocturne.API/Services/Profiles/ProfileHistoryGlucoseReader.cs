using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Profiles;

/// <summary>The canonical sensor glucose the profile history is read beside.</summary>
public sealed class ProfileHistoryGlucoseReader(
    ISensorGlucoseRepository sensorGlucose,
    IStatisticsService statistics)
{
    /// <summary>
    /// [<paramref name="fromMs"/>, <paramref name="toMs"/>) in <paramref name="buckets"/> equal buckets,
    /// oldest first. A bucket without readings is left out.
    /// </summary>
    public async Task<List<GlucoseTracePoint>> GetTraceAsync(long fromMs, long toMs, int buckets, CancellationToken ct)
    {
        if (toMs <= fromMs || buckets <= 0)
            return [];

        var width = Math.Max(1, (toMs - fromMs) / buckets);
        return
        [
            .. (await ReadAsync(fromMs, toMs, ct))
                .GroupBy(g => Math.Min((g.Mills - fromMs) / width, buckets - 1))
                .OrderBy(g => g.Key)
                .Select(g => new GlucoseTracePoint
                {
                    Mills = fromMs + g.Key * width + width / 2,
                    Average = ScaledRounding.Round(g.Average(r => r.Mgdl), 1),
                    Min = g.Min(r => r.Mgdl),
                    Max = g.Max(r => r.Mgdl),
                }),
        ];
    }

    /// <summary>
    /// The glucose of <paramref name="windowMs"/> before <paramref name="atMs"/> and of the same length
    /// after it, the later window ending at <paramref name="nowMs"/> at the latest.
    /// </summary>
    public async Task<GlucoseAroundChange> GetAroundAsync(long atMs, long windowMs, long nowMs, CancellationToken ct) => new()
    {
        Before = await SummaryAsync(atMs - windowMs, atMs, ct),
        After = await SummaryAsync(atMs, Math.Max(atMs, Math.Min(atMs + windowMs, nowMs)), ct),
    };

    private async Task<GlucoseWindowSummary> SummaryAsync(long fromMs, long toMs, CancellationToken ct)
    {
        var readings = toMs > fromMs ? await ReadAsync(fromMs, toMs, ct) : [];
        return new GlucoseWindowSummary
        {
            StartMills = fromMs,
            EndMills = toMs,
            Readings = readings.Count,
            Average = readings.Count > 0 ? ScaledRounding.Round(readings.Average(r => r.Mgdl), 1) : null,
            TimeInRange = readings.Count > 0
                ? ScaledRounding.Round(statistics.CalculateTimeInRange(readings).Percentages.Target, 1)
                : null,
        };
    }

    private async Task<List<SensorGlucose>> ReadAsync(long fromMs, long toMs, CancellationToken ct) =>
    [
        .. (await sensorGlucose.GetForAnalyticsAsync(
                DateTimeOffset.FromUnixTimeMilliseconds(fromMs).UtcDateTime,
                DateTimeOffset.FromUnixTimeMilliseconds(toMs).UtcDateTime,
                null, null, int.MaxValue, descending: false, ct: ct, streams: GlucoseStreams.Canonical))
            .Where(g => g.Mills >= fromMs && g.Mills < toMs),
    ];
}
