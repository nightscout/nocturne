using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Analytics;

/// <summary>
/// Builds the dataset for the actogram report (sleep, steps, heart-rate variants)
/// from glucose, sleep state spans, step counts, and heart rates within a window.
/// </summary>
/// <remarks>
/// Deliberately leaner than <see cref="IChartDataService"/>: no IOB/COB, basal series,
/// or treatment markers. Wide actogram windows (≥4 weeks) make those expensive and the
/// actogram does not render them.
/// </remarks>
public interface IActogramReportService
{
    /// <summary>
    /// Build the actogram report data for a given time range.
    /// </summary>
    /// <param name="startTime">Start of the time range in Unix milliseconds (inclusive).</param>
    /// <param name="endTime">End of the time range in Unix milliseconds (exclusive).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ActogramReportData> GetAsync(
        long startTime,
        long endTime,
        CancellationToken cancellationToken = default
    );
}
