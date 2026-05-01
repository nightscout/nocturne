using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Models;

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
    /// <summary>
    /// Get state spans with optional filtering
    /// </summary>
    /// <param name="category">Optional category filter</param>
    /// <param name="state">Optional state filter</param>
    /// <param name="from">Optional start time in milliseconds since Unix epoch</param>
    /// <param name="to">Optional end time in milliseconds since Unix epoch</param>
    /// <param name="source">Optional source filter</param>
    /// <param name="active">Optional active status filter</param>
    /// <param name="count">Maximum number of state spans to return</param>
    /// <param name="skip">Number of state spans to skip for pagination</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of state spans</returns>
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

    /// <summary>
    /// Count state spans matching the specified filters
    /// </summary>
    /// <param name="category">Optional category filter</param>
    /// <param name="state">Optional state filter</param>
    /// <param name="from">Optional start time filter</param>
    /// <param name="to">Optional end time filter</param>
    /// <param name="source">Optional source filter</param>
    /// <param name="active">Optional active status filter</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Total count of matching state spans</returns>
    Task<int> CountStateSpansAsync(
        StateSpanCategory? category = null,
        string? state = null,
        DateTime? from = null,
        DateTime? to = null,
        string? source = null,
        bool? active = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get a specific state span by ID
    /// </summary>
    /// <param name="id">State span ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>State span if found, null otherwise</returns>
    Task<StateSpan?> GetStateSpanByIdAsync(
        string id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Create or update a state span (upsert by originalId)
    /// </summary>
    /// <param name="stateSpan">State span to create or update</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created or updated state span</returns>
    Task<StateSpan> UpsertStateSpanAsync(
        StateSpan stateSpan,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete a state span
    /// </summary>
    /// <param name="id">State span ID to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if deleted successfully, false otherwise</returns>
    Task<bool> DeleteStateSpanAsync(
        string id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Update an existing state span
    /// </summary>
    /// <param name="id">State span ID to update</param>
    /// <param name="stateSpan">Updated state span data</param>
    /// <param name="cancellationToken">Cancellation token</param>
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
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<StateSpan?> GetActiveAtAsync(
        StateSpanCategory category,
        string? state,
        DateTime at,
        CancellationToken cancellationToken = default);

    #region Activity Compatibility Methods

    /// <summary>
    /// Get activities stored as StateSpans for v1 API compatibility
    /// </summary>
    /// <param name="type">Optional activity type filter</param>
    /// <param name="count">Maximum number of activities to return</param>
    /// <param name="skip">Number of activities to skip for pagination</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of activities</returns>
    Task<IEnumerable<Activity>> GetActivitiesAsync(
        string? type = null,
        int count = 10,
        int skip = 0,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get a specific activity by ID
    /// </summary>
    /// <param name="id">Activity ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Activity if found, null otherwise</returns>
    Task<Activity?> GetActivityByIdAsync(
        string id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Create activities and store as StateSpans
    /// </summary>
    /// <param name="activities">Activities to create</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created activities</returns>
    Task<IEnumerable<Activity>> CreateActivitiesAsync(
        IEnumerable<Activity> activities,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Update an existing activity (stored as StateSpan)
    /// </summary>
    /// <param name="id">Activity ID to update</param>
    /// <param name="activity">Updated activity data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated activity if successful, null otherwise</returns>
    Task<Activity?> UpdateActivityAsync(
        string id,
        Activity activity,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete an activity (stored as StateSpan)
    /// </summary>
    /// <param name="id">Activity ID to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if deleted successfully, false otherwise</returns>
    Task<bool> DeleteActivityAsync(
        string id,
        CancellationToken cancellationToken = default);

    #endregion
}
