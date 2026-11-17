using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.AidDetection;

/// <summary>
/// Calculates <see cref="AidSegmentMetrics"/> for AID systems that express algorithm activity
/// through temporary basal rate (TBR) records rather than APS snapshots.
/// </summary>
/// <remarks>
/// <see cref="AidDetectionContext.TempBasals"/> is every temp basal read from
/// <see cref="StatisticsService.TempBasalReadStart"/>, not only those inside the window: a record
/// counts for the time it ran inside the window, as <see cref="StatisticsService.DeliveredWithin"/>
/// decides, and what cancels or supersedes it can lie outside.
/// <see cref="AidSegmentMetrics.LoopCycleCount"/>, <see cref="AidSegmentMetrics.EnactedCount"/> and
/// <see cref="AidSegmentMetrics.ClosedLoopCount"/> are not applicable for this strategy and are returned as <see langword="null"/>.
/// </remarks>
/// <seealso cref="IAidDetectionStrategy"/>
public class TbrBasedStrategy : IAidDetectionStrategy
{
    /// <inheritdoc/>
    public IReadOnlySet<AidAlgorithm> SupportedAlgorithms { get; } = new HashSet<AidAlgorithm>
    {
        AidAlgorithm.ControlIQ,
        AidAlgorithm.CamAPSFX,
        AidAlgorithm.Omnipod5Algorithm,
        AidAlgorithm.MedtronicSmartGuard
    };

    /// <inheritdoc/>
    /// <remarks>
    /// Returns an empty <see cref="AidSegmentMetrics"/> when no temp basal delivered inside the window.
    /// </remarks>
    public AidSegmentMetrics CalculateMetrics(AidDetectionContext context)
    {
        var delivered = StatisticsService.DeliveredWithin(
            context.TempBasals, context.StartDate, context.EndDate.ToMills());

        if (delivered.Count == 0)
        {
            return new AidSegmentMetrics();
        }

        var total = context.EndDate - context.StartDate;

        return new AidSegmentMetrics
        {
            AidActivePercent = Percent(delivered.Where(t => t.Origin == TempBasalOrigin.Algorithm), total),
            PumpUsePercent = Percent(delivered, total),
            LoopCycleCount = null,
            EnactedCount = null,
            ClosedLoopCount = null
        };
    }

    private static double Percent(IEnumerable<TempBasal> delivered, TimeSpan total) =>
        IntervalUnion.Length(delivered.Select(t => (t.StartTimestamp, t.EndTimestamp!.Value))) / total * 100.0;
}
