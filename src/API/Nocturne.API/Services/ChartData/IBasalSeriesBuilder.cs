using Nocturne.Core.Clinical.Treatments;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.ChartData;

/// <summary>
/// Builds basal delivery series from TempBasal records and profile-inferred rates.
/// Gaps in TempBasal coverage are filled from <see cref="ScheduledBasal"/>,
/// stepping at its exact boundaries; when no TempBasal records exist the
/// entire series is inferred from it.
/// </summary>
public interface IBasalSeriesBuilder
{
    /// <summary>
    /// Build a basal delivery series for the given time window.
    /// </summary>
    /// <param name="tempBasals">
    /// Temp basals as delivered, as <c>StatisticsService.ClipOverlappingTempBasals</c> returns them (may be empty)
    /// </param>
    /// <param name="startTime">Start of the time range in Unix milliseconds</param>
    /// <param name="endTime">End of the time range in Unix milliseconds</param>
    /// <param name="defaultBasalRate">Fallback basal rate when no profile data exists</param>
    /// <param name="overrides">The override scale factors that scale the profile's rate, as <see cref="ScheduledBasal"/> reads them</param>
    Task<List<BasalPoint>> BuildAsync(
        List<TempBasal> tempBasals,
        long startTime,
        long endTime,
        double defaultBasalRate,
        IReadOnlyList<ScaleFactorInterval> overrides,
        CancellationToken ct = default
    );
}
