using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.AidDetection;

/// <summary>
/// Calculates <see cref="AidSegmentMetrics"/> for APS-based closed-loop algorithms from the
/// <see cref="AidDetectionContext.LoopCycles"/> within the evaluation window.
/// </summary>
/// <remarks>
/// Each loop cycle stands for the time from its timestamp to the next cycle, up to one 5-minute
/// cycle interval, so a gap between cycles is uncredited and a cycle repeated by a second uploader
/// or run at a faster cadence covers no extra time. AID-active percentage is the
/// <see cref="IntervalUnion"/> of the time of cycles enacted with the loop closed
/// (<see cref="ApsLoopCycle.EnactedClosedLoop"/>); pump-use percentage that of every cycle.
/// </remarks>
/// <seealso cref="IAidDetectionStrategy"/>
public class ApsSnapshotStrategy : IAidDetectionStrategy
{
    private static readonly TimeSpan LoopCycleInterval = TimeSpan.FromMinutes(5);

    /// <inheritdoc/>
    public IReadOnlySet<AidAlgorithm> SupportedAlgorithms { get; } = new HashSet<AidAlgorithm>
    {
        AidAlgorithm.OpenAps,
        AidAlgorithm.AndroidAps,
        AidAlgorithm.Loop,
        AidAlgorithm.Trio,
        AidAlgorithm.IAPS
    };

    /// <inheritdoc/>
    /// <remarks>
    /// Returns an empty <see cref="AidSegmentMetrics"/> when no APS snapshots are present
    /// in the supplied <paramref name="context"/>.
    /// </remarks>
    public AidSegmentMetrics CalculateMetrics(AidDetectionContext context)
    {
        // Of cycles at one instant only the last is credited, so a closed-loop copy goes last.
        var snapshots = context.LoopCycles.OrderBy(s => s.Timestamp).ThenBy(s => s.EnactedClosedLoop).ToList();

        if (snapshots.Count == 0)
        {
            return new AidSegmentMetrics();
        }

        var total = context.EndDate - context.StartDate;
        var cycles = new List<(DateTime Start, DateTime End)>(snapshots.Count);
        var closedCycles = new List<(DateTime Start, DateTime End)>();
        var enactedCount = 0;

        for (var i = 0; i < snapshots.Count; i++)
        {
            var start = snapshots[i].Timestamp;
            var end = start + LoopCycleInterval;
            if (i + 1 < snapshots.Count && snapshots[i + 1].Timestamp < end)
                end = snapshots[i + 1].Timestamp;

            var cycle = (
                start < context.StartDate ? context.StartDate : start,
                end > context.EndDate ? context.EndDate : end);

            cycles.Add(cycle);
            if (snapshots[i].Enacted)
                enactedCount++;
            if (snapshots[i].EnactedClosedLoop)
                closedCycles.Add(cycle);
        }

        return new AidSegmentMetrics
        {
            LoopCycleCount = snapshots.Count,
            EnactedCount = enactedCount,
            ClosedLoopCount = closedCycles.Count,
            AidActivePercent = IntervalUnion.Length(closedCycles) / total * 100.0,
            PumpUsePercent = IntervalUnion.Length(cycles) / total * 100.0
        };
    }
}
