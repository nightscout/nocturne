using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts;

/// <summary>
/// Service for creating tracker reset suggestions based on detected events.
/// Suggests resetting trackers when matching treatments are logged or sensor warmup is detected.
/// </summary>
public interface ITrackerSuggestionService
{
    /// <summary>
    /// Evaluate a treatment for potential tracker reset suggestions.
    /// Creates a SuggestedTrackerMatch notification if:
    /// - Treatment is a "Site Change" and user has a Cannula-category tracker
    /// </summary>
    /// <param name="treatment">The treatment that was created</param>
    /// <param name="userId">The user ID who owns the treatment</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task EvaluateTreatmentForTrackerSuggestionAsync(
        Treatment treatment,
        string userId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Evaluate a data gap for potential sensor tracker reset suggestions.
    /// Creates a SuggestedTrackerMatch notification if:
    /// - Gap is 60+ minutes
    /// - User has an active Sensor-category tracker
    /// - Tracker is within 8 hours of expected end or already past expiration
    /// </summary>
    /// <param name="userId">The user ID</param>
    /// <param name="gapStart">When the data gap started</param>
    /// <param name="gapEnd">When data resumed (or current time if still in gap)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task EvaluateDataGapForTrackerSuggestionAsync(
        string userId,
        DateTime gapStart,
        DateTime gapEnd,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Accept a tracker suggestion - completes the current instance (if any) and starts a new one
    /// </summary>
    /// <param name="notificationId">The notification ID to accept</param>
    /// <param name="userId">The user ID executing the action</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the tracker was reset successfully</returns>
    Task<bool> AcceptSuggestionAsync(
        Guid notificationId,
        string userId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Dismiss a tracker suggestion without taking action
    /// </summary>
    /// <param name="notificationId">The notification ID to dismiss</param>
    /// <param name="userId">The user ID executing the action</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if dismissed successfully</returns>
    Task<bool> DismissSuggestionAsync(
        Guid notificationId,
        string userId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Evaluate new entry data arrival to detect if data has resumed after a gap.
    /// If the previous data was 60+ minutes ago and the user has a Sensor-category tracker
    /// near end-of-life, creates a SuggestedTrackerMatch notification (likely sensor warmup completed).
    /// </summary>
    /// <param name="userId">The user ID</param>
    /// <param name="previousDataTime">When the previous data point was received (null if unknown)</param>
    /// <param name="newDataTime">When the new data point arrived</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task EvaluateDataResumedForTrackerSuggestionAsync(
        string userId,
        DateTime? previousDataTime,
        DateTime newDataTime,
        CancellationToken cancellationToken = default
    );
}
