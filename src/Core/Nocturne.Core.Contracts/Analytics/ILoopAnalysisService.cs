using Nocturne.Core.Models.LoopAnalysis;

namespace Nocturne.Core.Contracts.Analytics;

/// <summary>
/// Loads what a Loop Analysis page reads, once per request, and computes it for the primary range
/// and the comparison range.
/// </summary>
public interface ILoopAnalysisService
{
    /// <remarks>
    /// <paramref name="filter"/> is resolved: its ranges are instants in <paramref name="zone"/>
    /// that end after they start. The result is unredacted and carries none of the
    /// <see cref="LoopAnalysisResponse"/> envelope, which the caller fills, but its
    /// <see cref="LoopAnalysisResponse.ProfileNames"/>: every page that loads the profiles gives them, and
    /// one that does not reads them only with <see cref="LoopAnalysisFilter.ReadProfileNames"/>.
    /// </remarks>
    Task<DailyProfileResponse> GetDailyProfileAsync(
        LoopAnalysisFilter filter, TimeZoneInfo zone, CancellationToken ct = default);

    /// <remarks>Takes and returns as <see cref="GetDailyProfileAsync"/> does.</remarks>
    Task<PredictionAccuracyResponse> GetPredictionAccuracyAsync(
        LoopAnalysisFilter filter, TimeZoneInfo zone, CancellationToken ct = default);

    /// <remarks>Takes and returns as <see cref="GetDailyProfileAsync"/> does.</remarks>
    Task<LoopHealthResponse> GetLoopHealthAsync(
        LoopAnalysisFilter filter, TimeZoneInfo zone, CancellationToken ct = default);

    /// <summary>
    /// The names of the profiles active in the filter's ranges, oldest first, as a page's
    /// <c>ProfileNames</c> holds them, without loading anything else: for a page withheld whole.
    /// </summary>
    Task<List<string>> GetProfileNamesAsync(LoopAnalysisFilter filter, CancellationToken ct = default);

    /// <remarks>Takes and returns as <see cref="GetDailyProfileAsync"/> does; lists episodes for the primary range only.</remarks>
    Task<SettingsEvidenceReport> GetSettingsEvidenceAsync(
        LoopAnalysisFilter filter, TimeZoneInfo zone, CancellationToken ct = default);

    /// <inheritdoc cref="GetDailyProfileAsync"/>
    /// <param name="definition">The cohorts and window, already narrowed to the caller.</param>
    Task<EventResponseReport> GetEventResponseAsync(
        LoopAnalysisFilter filter, EventResponseDefinition definition, TimeZoneInfo zone, CancellationToken ct = default);
}
