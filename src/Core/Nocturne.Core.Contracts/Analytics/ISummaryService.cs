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
    Task<SummaryResponse> GetSummaryAsync(
        int hours = 6,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Filters SGV entries to the time window and reduces them to the summary fields.
    /// </summary>
    List<SummarySgv> ProcessSgvs(List<Entry> sgvs, int hours);

    /// <summary>
    /// Extracts insulin and carb treatments and processes temp basals.
    /// </summary>
    /// <param name="profileData">Current profile data for basal processing</param>
    SummaryTreatments ProcessTreatments(
        List<Treatment> treatments,
        ProfileData? profileData,
        int hours
    );

    /// <summary>
    /// Port of legacy <c>basaldataprocessor.js</c>.
    /// </summary>
    /// <param name="profileData">Profile data containing basal schedule</param>
    /// <param name="tempBasals">List of temp basal treatments</param>
    /// <param name="dataCap">Timestamp cutoff for data inclusion</param>
    List<SummaryTempBasal> ProcessTempBasals(
        ProfileData? profileData,
        List<Treatment> tempBasals,
        long dataCap
    );

    /// <summary>
    /// Port of legacy <c>constructState()</c>, built from the sandbox properties.
    /// </summary>
    Task<SummaryState> ConstructStateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Merges consecutive temp basals with the same absolute rate; port of legacy
    /// <c>filterSameAbsTemps</c>.
    /// </summary>
    List<SummaryTempBasal> FilterSameAbsoluteTemps(List<SummaryTempBasal> tempData);

    /// <summary>
    /// Profile basals for the gaps between temp basals; port of legacy
    /// <c>_profileBasalsInWindow</c>.
    /// </summary>
    /// <returns>Profile basal entries for the time window</returns>
    List<SummaryTempBasal> GetProfileBasalsInWindow(List<TimeValue>? basals, long start, long end);
}
