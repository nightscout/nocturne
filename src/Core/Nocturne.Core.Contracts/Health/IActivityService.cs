using Nocturne.Core.Models;
using Nocturne.Core.Contracts.Glucose;

namespace Nocturne.Core.Contracts.Health;

/// <summary>
/// Domain service for <see cref="Activity"/> operations with WebSocket broadcasting.
/// </summary>
/// <seealso cref="Activity"/>
/// <seealso cref="IStateSpanService"/>
public interface IActivityService
{
    /// <summary>
    /// Get <see cref="Activity"/> records with optional filtering and pagination.
    /// </summary>
    /// <param name="find">Optional MongoDB-style query filter string (e.g., <c>find[type][$eq]=exercise</c>).</param>
    /// <param name="count">Maximum number of records to return.</param>
    /// <param name="skip">Number of records to skip for pagination.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Collection of <see cref="Activity"/> records matching the filter criteria.</returns>
    Task<IEnumerable<Activity>> GetActivitiesAsync(
        string? find = null,
        int? count = null,
        int? skip = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get a specific <see cref="Activity"/> record by ID.
    /// </summary>
    /// <param name="id">Activity ID (legacy MongoDB ObjectId or UUID v7 string).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The <see cref="Activity"/> if found; <c>null</c> otherwise.</returns>
    Task<Activity?> GetActivityByIdAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Create new <see cref="Activity"/> records with WebSocket broadcasting.
    /// </summary>
    /// <remarks>Broadcasts a storage-create event via SignalR after persistence.</remarks>
    /// <param name="activities"><see cref="Activity"/> records to create.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Created <see cref="Activity"/> records with assigned IDs.</returns>
    Task<IEnumerable<Activity>> CreateActivitiesAsync(
        IEnumerable<Activity> activities,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Update an existing <see cref="Activity"/> record with WebSocket broadcasting.
    /// </summary>
    /// <remarks>Broadcasts a storage-update event via SignalR after persistence.</remarks>
    /// <param name="id">Activity ID to update.</param>
    /// <param name="activity">Updated <see cref="Activity"/> data.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated <see cref="Activity"/> if successful; <c>null</c> if not found.</returns>
    Task<Activity?> UpdateActivityAsync(
        string id,
        Activity activity,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Delete an <see cref="Activity"/> record with WebSocket broadcasting.
    /// </summary>
    /// <remarks>Broadcasts a storage-delete event via SignalR after persistence.</remarks>
    /// <param name="id">Activity ID to delete.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> if deleted successfully; <c>false</c> if not found.</returns>
    Task<bool> DeleteActivityAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete multiple <see cref="Activity"/> records with optional filtering.
    /// </summary>
    /// <param name="find">Optional MongoDB-style query filter string.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of records deleted.</returns>
    Task<long> DeleteMultipleActivitiesAsync(
        string? find = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Count <see cref="Activity"/> records across all decomposed sources (StateSpans, HeartRate, StepCount).
    /// </summary>
    /// <param name="find">Optional MongoDB-style query filter string.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Total number of activity records matching the filter.</returns>
    Task<long> CountActivitiesAsync(
        string? find = null,
        CancellationToken cancellationToken = default
    );
}
