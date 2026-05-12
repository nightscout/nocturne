using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.Analytics;

/// <summary>
/// Builds basal delivery series from TempBasal records and profile-inferred rates.
/// Gaps in TempBasal coverage are filled at 5-minute resolution from the active
/// basal rate schedule; when no TempBasal records exist the entire series is
/// profile-derived.
/// </summary>
public interface IBasalSeriesBuilder
{
    /// <summary>
    /// Build a basal delivery series for the given time window.
    /// </summary>
    /// <param name="tempBasals">Pump-confirmed TempBasal records (may be empty)</param>
    /// <param name="startTime">Start of the time range in Unix milliseconds</param>
    /// <param name="endTime">End of the time range in Unix milliseconds</param>
    /// <param name="defaultBasalRate">Fallback basal rate when no profile data exists</param>
    /// <param name="ct">Cancellation token</param>
    Task<List<BasalPoint>> BuildAsync(
        List<TempBasal> tempBasals,
        long startTime,
        long endTime,
        double defaultBasalRate,
        CancellationToken ct = default
    );
}
