using Nocturne.Connectors.Core.Services;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Services;

namespace Nocturne.Connectors.Glooko.Services;

/// <summary>
///     Custom stats provider for the Glooko connector.
///     Glooko syncs periodically (every few hours) and produces data across multiple tables
///     (CGM, BG checks, boluses, carb intakes, food entries, state spans). The default
///     glucose-only stats would undercount records and show "inactive" between syncs.
/// </summary>
public class GlookoStatsProvider : ConnectorStatsProvider
{
    /// <summary>
    ///     Uses comprehensive stats across all V4 tables with a 6-hour stale threshold
    ///     appropriate for a batch connector that syncs every few hours.
    /// </summary>
    public override void ApplyStats(DataSourceInfo info, DataSourceStats stats, DateTimeOffset now)
    {
        // Use total items across all tables (glucose + treatments + state spans)
        info.TotalEntries = stats.TotalItems;
        info.EntriesLast24Hours = stats.ItemsLast24Hours;

        // Use the most recent item time across all tables
        if (stats.LastItemTime.HasValue)
        {
            var lastItemOffset = new DateTimeOffset(stats.LastItemTime.Value, TimeSpan.Zero);
            if (!info.LastSeen.HasValue || lastItemOffset > info.LastSeen.Value)
                info.LastSeen = lastItemOffset;
        }

        // Use the earliest item time for FirstSeen
        var firstTimes = new[] { stats.FirstEntryTime, stats.FirstTreatmentTime, stats.FirstStateSpanTime }
            .Where(t => t.HasValue)
            .ToArray();
        if (firstTimes.Length > 0)
        {
            var earliest = new DateTimeOffset(firstTimes.Min()!.Value, TimeSpan.Zero);
            if (!info.FirstSeen.HasValue || earliest < info.FirstSeen.Value)
                info.FirstSeen = earliest;
        }

        // Batch connector thresholds: 3h active, 6h stale, then inactive
        var minutesSinceLast = info.LastSeen.HasValue
            ? (int)(now - info.LastSeen.Value).TotalMinutes
            : int.MaxValue;
        info.MinutesSinceLastData = minutesSinceLast;
        info.Status = minutesSinceLast switch
        {
            < 180 => "active",
            < 360 => "stale",
            _ => "inactive",
        };
    }
}
