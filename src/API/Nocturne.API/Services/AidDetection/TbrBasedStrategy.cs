using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.AidDetection;

/// <summary>
/// Calculates <see cref="AidSegmentMetrics"/> for AID systems that express algorithm activity
/// through temporary basal rate (TBR) records rather than APS snapshots.
/// </summary>
/// <remarks>
/// Durations are clamped to the evaluation window defined by
/// <see cref="AidDetectionContext.StartDate"/> and <see cref="AidDetectionContext.EndDate"/>.
/// TBRs without an explicit end time are assumed to last <c>5</c> minutes. Overlapping TBRs
/// cover their shared time once, as their <see cref="IntervalUnion"/>.
/// <see cref="AidSegmentMetrics.LoopCycleCount"/> and <see cref="AidSegmentMetrics.EnactedCount"/>
/// are not applicable for this strategy and are returned as <see langword="null"/>.
/// </remarks>
/// <seealso cref="IAidDetectionStrategy"/>
public class TbrBasedStrategy : IAidDetectionStrategy
{
    private const double DefaultDurationMinutes = 5.0;

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
    /// Returns an empty <see cref="AidSegmentMetrics"/> when no TBR records are present
    /// in the supplied <paramref name="context"/>.
    /// </remarks>
    public AidSegmentMetrics CalculateMetrics(AidDetectionContext context)
    {
        var tempBasals = context.TempBasals;

        if (tempBasals.Count == 0)
        {
            return new AidSegmentMetrics();
        }

        var total = context.EndDate - context.StartDate;
        var all = tempBasals
            .Select(tbr => (Tbr: tbr, Interval: ClampedInterval(tbr, context.StartDate, context.EndDate)))
            .ToList();

        return new AidSegmentMetrics
        {
            AidActivePercent = IntervalUnion.Length(all
                .Where(t => t.Tbr.Origin == TempBasalOrigin.Algorithm)
                .Select(t => t.Interval)) / total * 100.0,
            PumpUsePercent = IntervalUnion.Length(all.Select(t => t.Interval)) / total * 100.0,
            LoopCycleCount = null,
            EnactedCount = null
        };
    }

    private static (DateTime Start, DateTime End) ClampedInterval(TempBasal tbr, DateTime windowStart, DateTime windowEnd)
    {
        var end = tbr.EndTimestamp ?? tbr.StartTimestamp.AddMinutes(DefaultDurationMinutes);
        return (
            tbr.StartTimestamp < windowStart ? windowStart : tbr.StartTimestamp,
            end > windowEnd ? windowEnd : end);
    }
}
