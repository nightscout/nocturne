using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Repositories;

/// <summary>
/// Repository port for <see cref="StateSpan"/> operations. State spans represent
/// time-bounded states such as temporary basals, profile switches, exercise sessions,
/// and other duration-based events.
/// </summary>
/// <seealso cref="StateSpan"/>
/// <seealso cref="StateSpanCategory"/>
public interface IStateSpanRepository
{
    /// <summary>
    /// Queries state spans with optional filtering by category, state, time range, source, and active status.
    /// </summary>
    /// <param name="category">Optional <see cref="StateSpanCategory"/> filter.</param>
    /// <param name="state">Optional state string filter.</param>
    /// <param name="from">Optional start of the time range (inclusive).</param>
    /// <param name="to">Optional end of the time range (inclusive).</param>
    /// <param name="source">Optional data source filter.</param>
    /// <param name="active">When set, filters to only active or only ended spans.</param>
    /// <param name="count">Maximum number of records to return.</param>
    /// <param name="skip">Number of records to skip for pagination.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A collection of matching <see cref="StateSpan"/> records.</returns>
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
    /// Counts state spans matching the specified filters.
    /// </summary>
    /// <param name="category">Optional <see cref="StateSpanCategory"/> filter.</param>
    /// <param name="state">Optional state string filter.</param>
    /// <param name="from">Optional start of the time range (inclusive).</param>
    /// <param name="to">Optional end of the time range (inclusive).</param>
    /// <param name="source">Optional data source filter.</param>
    /// <param name="active">When set, filters to only active or only ended spans.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The total number of matching records.</returns>
    Task<int> CountStateSpansAsync(
        StateSpanCategory? category = null,
        string? state = null,
        DateTime? from = null,
        DateTime? to = null,
        string? source = null,
        bool? active = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a single state span by its identifier.
    /// </summary>
    /// <param name="id">The state span identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The <see cref="StateSpan"/> if found, or <c>null</c>.</returns>
    Task<StateSpan?> GetStateSpanByIdAsync(
        string id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or updates a state span, matched by its identifier or original ID.
    /// </summary>
    /// <param name="stateSpan">The <see cref="StateSpan"/> to upsert.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted <see cref="StateSpan"/>.</returns>
    Task<StateSpan> UpsertStateSpanAsync(
        StateSpan stateSpan,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Bulk upserts multiple state spans, typically used by connector imports.
    /// </summary>
    /// <param name="stateSpans">The state spans to upsert.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of records upserted.</returns>
    Task<int> BulkUpsertAsync(
        IEnumerable<StateSpan> stateSpans,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing state span by ID.
    /// </summary>
    /// <param name="id">The state span identifier.</param>
    /// <param name="stateSpan">The updated <see cref="StateSpan"/> data.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated <see cref="StateSpan"/>, or <c>null</c> if not found.</returns>
    Task<StateSpan?> UpdateStateSpanAsync(
        string id,
        StateSpan stateSpan,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a state span by ID.
    /// </summary>
    /// <param name="id">The state span identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> if deleted; <c>false</c> if not found.</returns>
    Task<bool> DeleteStateSpanAsync(
        string id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes all state spans originating from the specified data source.
    /// </summary>
    /// <param name="source">The data source identifier (e.g., "demo-service").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of records deleted.</returns>
    Task<long> DeleteBySourceAsync(
        string source,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the <see cref="StateSpan"/> of <paramref name="category"/> that contains
    /// <paramref name="at"/> (<c>StartTimestamp &lt;= at &lt; EndTimestamp</c>),
    /// optionally filtered by <paramref name="state"/>. When multiple spans overlap the
    /// instant, the one with the most recent <c>StartTimestamp</c> wins. Returns
    /// <c>null</c> if no span is active.
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

    /// <summary>
    /// Returns all state spans in the specified category within an optional time range.
    /// </summary>
    /// <param name="category">The <see cref="StateSpanCategory"/> to filter by.</param>
    /// <param name="from">Optional start of the time range (inclusive).</param>
    /// <param name="to">Optional end of the time range (inclusive).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A collection of matching <see cref="StateSpan"/> records.</returns>
    Task<IEnumerable<StateSpan>> GetByCategory(
        StateSpanCategory category,
        DateTime? from = null,
        DateTime? to = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns state spans grouped by category for multiple categories within an optional time range.
    /// </summary>
    /// <param name="categories">The set of <see cref="StateSpanCategory"/> values to query.</param>
    /// <param name="from">Optional start of the time range (inclusive).</param>
    /// <param name="to">Optional end of the time range (inclusive).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A dictionary mapping each category to its matching <see cref="StateSpan"/> records.</returns>
    Task<Dictionary<StateSpanCategory, List<StateSpan>>> GetByCategories(
        IEnumerable<StateSpanCategory> categories,
        DateTime? from = null,
        DateTime? to = null,
        CancellationToken cancellationToken = default);

    // Activity Compatibility Methods

    /// <summary>
    /// Returns activity-category state spans with optional type filtering and pagination.
    /// Compatibility layer for the legacy Activity API.
    /// </summary>
    /// <param name="type">Optional activity type filter.</param>
    /// <param name="count">Maximum number of records to return.</param>
    /// <param name="skip">Number of records to skip for pagination.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A collection of activity <see cref="StateSpan"/> records.</returns>
    Task<IEnumerable<StateSpan>> GetActivityStateSpansAsync(
        string? type = null,
        int count = 10,
        int skip = 0,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a single activity state span by its identifier.
    /// </summary>
    /// <param name="id">The state span identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The activity <see cref="StateSpan"/> if found, or <c>null</c>.</returns>
    Task<StateSpan?> GetActivityStateSpanByIdAsync(
        string id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or updates an activity as a state span.
    /// </summary>
    /// <param name="stateSpan">The <see cref="StateSpan"/> representing the activity.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted <see cref="StateSpan"/>.</returns>
    Task<StateSpan> UpsertActivityAsStateSpanAsync(
        StateSpan stateSpan,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates multiple activities as state spans.
    /// </summary>
    /// <param name="stateSpans">The state spans representing activities to create.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A collection of the created <see cref="StateSpan"/> records.</returns>
    Task<IEnumerable<StateSpan>> CreateActivitiesAsStateSpansAsync(
        IEnumerable<StateSpan> stateSpans,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an activity state span by ID.
    /// </summary>
    /// <param name="id">The state span identifier.</param>
    /// <param name="stateSpan">The updated <see cref="StateSpan"/> data.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated <see cref="StateSpan"/>, or <c>null</c> if not found.</returns>
    Task<StateSpan?> UpdateActivityStateSpanAsync(
        string id,
        StateSpan stateSpan,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes an activity state span by ID.
    /// </summary>
    /// <param name="id">The state span identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> if deleted; <c>false</c> if not found.</returns>
    Task<bool> DeleteActivityStateSpanAsync(
        string id,
        CancellationToken cancellationToken = default);
}
