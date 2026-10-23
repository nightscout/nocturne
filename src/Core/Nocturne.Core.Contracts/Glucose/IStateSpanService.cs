using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.Glucose;

/// <summary>
/// Domain service for <see cref="StateSpan"/> lifecycle operations.
/// State spans represent time-bounded device or therapy states such as temp basals,
/// profile switches, exercise sessions, and sensor data exclusions.
/// </summary>
/// <remarks>
/// The legacy Activity collection is stored as <see cref="StateSpan"/> records internally.
/// The <c>GetActivitiesAsync</c>, <c>GetActivityByIdAsync</c>, <c>CreateActivitiesAsync</c>,
/// <c>UpdateActivityAsync</c>, and <c>DeleteActivityAsync</c> overloads provide backward-compatible
/// access for callers that work with the v1 activities API.
/// </remarks>
/// <seealso cref="StateSpan"/>
/// <seealso cref="StateSpanCategory"/>
/// <seealso cref="IActivityService"/>
/// <seealso cref="Nocturne.Core.Contracts.Repositories.IStateSpanRepository"/>
public interface IStateSpanService
{
    /// <param name="category">Optional category filter</param>
    /// <param name="state">Optional state filter</param>
    /// <param name="from">Optional start time in milliseconds since Unix epoch</param>
    /// <param name="to">Optional end time in milliseconds since Unix epoch</param>
    /// <param name="source">Optional source filter</param>
    /// <param name="active">Optional active status filter</param>
    /// <param name="count">Maximum number of state spans to return</param>
    /// <param name="skip">Number of state spans to skip for pagination</param>
    Task<IEnumerable<StateSpan>> GetStateSpansAsync(
        StateSpanCategory? category = null,
        string? state = null,
        DateTime? from = null,
        DateTime? to = null,
        string? source = null,
        bool? active = null,
        int count = 100,
        int skip = 0,
        bool descending = true,
        CancellationToken cancellationToken = default);

    /// <inheritdoc cref="Nocturne.Core.Contracts.Repositories.IStateSpanRepository.GetStateSpansStartingFromAsync"/>
    Task<IEnumerable<StateSpan>> GetStateSpansStartingFromAsync(
        StateSpanCategory category,
        string? source,
        DateTime from,
        int count,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the current pump operational mode, derived from the most recently started
    /// open-ended <see cref="StateSpanCategory.PumpMode"/> span.
    /// </summary>
    Task<PumpModeState?> GetCurrentPumpModeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Count state spans matching the specified filters
    /// </summary>
    /// <param name="category">Optional category filter</param>
    /// <param name="state">Optional state filter</param>
    /// <param name="from">Optional start time filter</param>
    /// <param name="to">Optional end time filter</param>
    /// <param name="source">Optional source filter</param>
    /// <param name="active">Optional active status filter</param>
    /// <returns>Total count of matching state spans</returns>
    Task<int> CountStateSpansAsync(
        StateSpanCategory? category = null,
        string? state = null,
        DateTime? from = null,
        DateTime? to = null,
        string? source = null,
        bool? active = null,
        CancellationToken cancellationToken = default);

    /// <returns>State span if found, null otherwise</returns>
    Task<StateSpan?> GetStateSpanByIdAsync(
        string id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Create or update a state span (upsert by originalId)
    /// </summary>
    /// <param name="stateSpan">State span to create or update</param>
    /// <returns>Created or updated state span</returns>
    Task<StateSpan> UpsertStateSpanAsync(
        StateSpan stateSpan,
        CancellationToken cancellationToken = default);

    /// <returns>True if deleted successfully, false otherwise</returns>
    Task<bool> DeleteStateSpanAsync(
        string id,
        CancellationToken cancellationToken = default);

    /// <inheritdoc cref="Nocturne.Core.Contracts.Repositories.IStateSpanRepository.ResolveDeletedAsync"/>
    Task<IReadOnlyDictionary<Guid, Nocturne.Core.Contracts.Repositories.DeletedStateSpan>> ResolveDeletedAsync(
        IReadOnlyCollection<string> ids,
        CancellationToken cancellationToken = default);

    /// <inheritdoc cref="Nocturne.Core.Contracts.Repositories.IStateSpanRepository.RestoreAsync"/>
    Task<StateSpan> RestoreStateSpanAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    /// <inheritdoc cref="Nocturne.Core.Contracts.Repositories.IStateSpanRepository.BulkRestoreAsync"/>
    Task<BulkRestoreResult<StateSpan>> BulkRestoreStateSpansAsync(
        IEnumerable<Guid> ids,
        CancellationToken cancellationToken = default);

    /// <param name="stateSpan">Updated state span data</param>
    /// <returns>Updated state span if successful, null otherwise</returns>
    Task<StateSpan?> UpdateStateSpanAsync(
        string id,
        StateSpan stateSpan,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the <see cref="StateSpan"/> of <paramref name="category"/> that contains
    /// <paramref name="at"/> (<c>StartTimestamp &lt;= at &lt; EndTimestamp</c>),
    /// optionally filtered by <paramref name="state"/>. Latest <c>StartTimestamp</c> wins
    /// on overlap; returns <c>null</c> if no span is active.
    /// </summary>
    /// <param name="category">The category to filter by.</param>
    /// <param name="state">Optional <c>State</c> filter; <c>null</c> matches any state.</param>
    /// <param name="at">The instant to evaluate.</param>
    Task<StateSpan?> GetActiveAtAsync(
        StateSpanCategory category,
        string? state,
        DateTime at,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get activities stored as StateSpans for v1 API compatibility
    /// </summary>
    /// <param name="count">Maximum number of activities to return</param>
    /// <param name="skip">Number of activities to skip for pagination</param>
    /// <param name="from">Inclusive lower bound on the activity's start, or <c>null</c> for none</param>
    /// <param name="to">Inclusive upper bound on the activity's start, or <c>null</c> for none</param>
    /// <returns>Collection of activities, newest first</returns>
    Task<IEnumerable<Activity>> GetActivitiesAsync(
        int count = 10,
        int skip = 0,
        DateTime? from = null,
        DateTime? to = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the latest start timestamp across activity-category state spans written by
    /// <paramref name="source"/>, or <c>null</c> when it has written none.
    /// </summary>
    /// <param name="source">The data source to scope to.</param>
    Task<DateTime?> GetLatestActivityTimestampAsync(
        string source,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the latest start timestamp across the state spans written by
    /// <paramref name="source"/> outside the activity categories, or <c>null</c> when it has
    /// written none.
    /// </summary>
    /// <param name="source">The data source to scope to.</param>
    Task<DateTime?> GetLatestNonActivityTimestampAsync(
        string source,
        CancellationToken cancellationToken = default);

    /// <returns>Activity if found, null otherwise</returns>
    Task<Activity?> GetActivityByIdAsync(
        string id,
        CancellationToken cancellationToken = default);

    Task<IEnumerable<Activity>> CreateActivitiesAsync(
        IEnumerable<Activity> activities,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Update an existing activity (stored as StateSpan)
    /// </summary>
    /// <param name="activity">Updated activity data</param>
    /// <returns>Updated activity if successful, null otherwise</returns>
    Task<Activity?> UpdateActivityAsync(
        string id,
        Activity activity,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete an activity (stored as StateSpan), with every other copy in its duplicate group
    /// </summary>
    /// <returns>
    /// The activity ids deleted: <paramref name="id"/> as given, then the copies'. Empty if not found.
    /// </returns>
    Task<IReadOnlyList<string>> DeleteActivityAsync(
        string id,
        CancellationToken cancellationToken = default);
}
