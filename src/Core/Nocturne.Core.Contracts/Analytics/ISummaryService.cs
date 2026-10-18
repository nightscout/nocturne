using Nocturne.Core.Models;
using Nocturne.Core.Contracts.Glucose;

namespace Nocturne.Core.Contracts.Analytics;

/// <summary>
/// Service interface for summary operations with 1:1 legacy JavaScript compatibility.
/// Provides summary data aggregation and processing functionality.
/// </summary>
/// <seealso cref="IWidgetSummaryService"/>
/// <seealso cref="IChartDataService"/>
/// <seealso cref="IEntryService"/>
public interface ISummaryService
{
    /// <summary>
    /// Gets summary data for the specified number of hours
    /// </summary>
    /// <param name="hours">Number of hours to include in summary (default 6)</param>
    /// <returns>Summary response containing processed data</returns>
    Task<SummaryResponse> GetSummaryAsync(
        int hours = 6,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Processes SGV entries for the summary endpoint
    /// Filters by time window and simplifies to essential glucose data
    /// </summary>
    /// <param name="sgvs">List of SGV entries to process</param>
    /// <param name="hours">Number of hours to include</param>
    /// <returns>Processed SGV entries</returns>
    List<SummarySgv> ProcessSgvs(List<Entry> sgvs, int hours);

    /// <summary>
    /// Processes treatments for the summary endpoint
    /// Extracts insulin/carbs treatments and processes temp basals
    /// </summary>
    /// <param name="profileData">Current profile data for basal processing</param>
    /// <param name="hours">Number of hours to include</param>
    /// <returns>Processed treatment data</returns>
    SummaryTreatments ProcessTreatments(
        List<Treatment> treatments,
        ProfileData? profileData,
        int hours
    );

    /// <summary>
    /// Processes temporary basals using profile data
    /// Implements equivalent logic to legacy basaldataprocessor.js
    /// </summary>
    /// <param name="profileData">Profile data containing basal schedule</param>
    /// <param name="tempBasals">List of temp basal treatments</param>
    /// <param name="dataCap">Timestamp cutoff for data inclusion</param>
    /// <returns>Processed temp basal entries</returns>
    List<SummaryTempBasal> ProcessTempBasals(
        ProfileData? profileData,
        List<Treatment> tempBasals,
        long dataCap
    );

    /// <summary>
    /// Constructs state information from sandbox properties
    /// Equivalent to legacy constructState() function
    /// </summary>
    Task<SummaryState> ConstructStateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Filters and merges consecutive temp basals with same absolute rate
    /// Implements equivalent logic to legacy filterSameAbsTemps
    /// </summary>
    /// <param name="tempData">List of temp basals to filter</param>
    /// <returns>Filtered temp basal list</returns>
    List<SummaryTempBasal> FilterSameAbsoluteTemps(List<SummaryTempBasal> tempData);

    /// <summary>
    /// Generates profile basals for gaps between temp basals
    /// Implements equivalent logic to legacy _profileBasalsInWindow
    /// </summary>
    /// <param name="basals">Profile basal schedule</param>
    /// <param name="start">Start timestamp</param>
    /// <param name="end">End timestamp</param>
    /// <returns>Profile basal entries for the time window</returns>
    List<SummaryTempBasal> GetProfileBasalsInWindow(List<TimeValue>? basals, long start, long end);
}
