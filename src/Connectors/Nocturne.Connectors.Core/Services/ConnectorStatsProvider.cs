using Nocturne.Core.Models;
using Nocturne.Core.Models.Services;

namespace Nocturne.Connectors.Core.Services;

/// <summary>
///     Provides customizable stats presentation for a connector's data source entry
///     in the Active Data Sources UI. Connectors can subclass this to override how
///     their stats (total entries, last seen, status thresholds) are calculated from
///     the raw <see cref="DataSourceStats"/>.
/// </summary>
/// <remarks>
///     The default implementation uses all data across all tables with a 6-hour
///     stale threshold (appropriate for batch/periodic connectors). Real-time uploaders
///     (xDrip, Loop) don't use this — they use the built-in 15-minute threshold.
/// </remarks>
public class ConnectorStatsProvider
{
    /// <summary>
    ///     Singleton default provider used when a connector doesn't specify a custom one.
    /// </summary>
    public static ConnectorStatsProvider Default { get; } = new();

    /// <summary>
    ///     Applies stats from <paramref name="stats"/> to the <paramref name="info"/> data source entry.
    ///     Override this method in a connector-specific subclass to customize which stats
    ///     are displayed and how the active/stale/inactive status is determined.
    /// </summary>
    /// <param name="info">The data source info to populate. May already have glucose-only stats set.</param>
    /// <param name="stats">Comprehensive stats across all V4 tables for this data source.</param>
    /// <param name="now">Current time for calculating minutes-since-last.</param>
    public virtual void ApplyStats(DataSourceInfo info, DataSourceStats stats, DateTimeOffset now)
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

        // Apply status with batch connector thresholds (6h stale window)
        var minutesSinceLast = info.LastSeen.HasValue
            ? (int)(now - info.LastSeen.Value).TotalMinutes
            : int.MaxValue;
        info.MinutesSinceLastData = minutesSinceLast;
        info.Status = minutesSinceLast switch
        {
            < 60 => "active",
            < 360 => "stale",
            _ => "inactive",
        };
    }
}
